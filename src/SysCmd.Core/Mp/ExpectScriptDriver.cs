using SysCmd.Core.Configuration;
using SysCmd.Core.Events;

namespace SysCmd.Core.Mp;

/// <summary>
/// Drives a management processor by replaying the expect/send script from its mp-type file.
/// Every session takes an endpoint lease first, because a console-server port is a single wire
/// and an MP will happily refuse or corrupt a second concurrent login.
/// </summary>
public sealed class ExpectScriptDriver(EndpointBroker broker, EventLog events) : IMpDriver
{
    /// <summary>How long a power job waits for a console window to release an endpoint before giving up.</summary>
    private static readonly TimeSpan LeaseWait = TimeSpan.FromSeconds(15);

    public bool CanHandle(MpTypeDefinition type)
        => type.Transport.Equals("telnet", StringComparison.OrdinalIgnoreCase);

    public async Task<MpResult> RunTaskAsync(
        ConfigSnapshot snapshot, MachineConfig machine, string task,
        Action<string>? progress, CancellationToken ct)
    {
        if (machine.Mp is not { } mp)
            return new MpResult(false, PowerState.Unknown, "", $"{machine.Name} has no management processor configured.");

        if (snapshot.MpTypes.GetValueOrDefault(mp.Type) is not { } type)
            return new MpResult(false, PowerState.Unknown, "", $"MP type '{mp.Type}' is not defined.");

        if (!type.Tasks.TryGetValue(task, out var steps))
            return new MpResult(false, PowerState.Unknown, "", $"MP type '{mp.Type}' defines no '{task}' task.");

        if (EndpointResolver.ForMp(snapshot, machine) is not { } endpoint)
            return new MpResult(false, PowerState.Unknown, "", $"Could not resolve an address for {machine.Name}'s MP.");

        progress?.Invoke($"Connecting to {endpoint} ({type.Name})");

        // A busy endpoint is deliberately allowed to propagate: the caller needs to know the wire
        // is held by someone else, not retry into a queue behind an idle console window.
        var exclusive = EndpointResolver.RequiresExclusiveSession(snapshot, machine, ConsoleTarget.Mp);
        await using var lease = await broker.AcquireAsync(
            endpoint, $"{task} on {machine.Name}", LeaseWait, ct, exclusive);

        TelnetSession session;
        try
        {
            session = await TelnetSession.ConnectAsync(
                endpoint.Host, endpoint.Port, TimeSpan.FromSeconds(type.Timeouts.ConnectSeconds), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Reported rather than thrown, so a caller waiting for an MP to finish booting can
            // simply try again instead of unwinding the whole job.
            return new MpResult(false, PowerState.Unknown, "", $"Could not connect to {endpoint}: {ex.Message}")
            {
                Transient = true,
            };
        }

        await using var _ = session;
        var engine = new ExpectEngine(session);
        var vars = new ScriptVariables(mp.Username, mp.Password);

        // Everything before this flips is connect-and-login, where a failure means no command
        // reached the machine and the attempt can safely be repeated.
        var taskStarted = false;

        try
        {
            // A console-server port often lands mid-session with no banner until something is typed.
            // Listen first: sending a stray carriage return to a device that greets immediately
            // would be consumed as the username and desynchronise the whole login.
            if (mp.Via is not null)
            {
                await engine.DrainAsync(TimeSpan.FromSeconds(2), ct);
                if (engine.Transcript.Length == 0)
                {
                    await session.WriteAsync("\r", ct);
                    await engine.DrainAsync(TimeSpan.FromSeconds(2), ct);
                }
            }

            if (type.Login.Count > 0)
            {
                progress?.Invoke("Logging in");
                await engine.RunAsync(type.Login, vars, type.Timeouts, ct);
            }

            progress?.Invoke($"Running '{task}'");
            taskStarted = true;
            var state = await engine.RunAsync(steps, vars, type.Timeouts, ct);

            await TryLogoutAsync(engine, type, vars, ct);

            events.Write(EventLevel.Debug, "mp", $"{task} on {machine.Name} finished",
                machine.Id, detail: engine.Transcript);

            return new MpResult(true, state, engine.Transcript);
        }
        catch (ExpectTimeoutException ex)
        {
            events.Warn("mp", $"{task} on {machine.Name} timed out waiting for \"{ex.Pattern}\"",
                machine.Id, detail: engine.Transcript);
            return new MpResult(false, PowerState.Unknown, engine.Transcript, ex.Message)
            {
                Transient = !taskStarted,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            events.Warn("mp", $"{task} on {machine.Name} failed: {ex.Message}",
                machine.Id, detail: engine.Transcript);
            return new MpResult(false, PowerState.Unknown, engine.Transcript, ex.Message)
            {
                Transient = !taskStarted,
            };
        }
    }

    /// <summary>Leaving cleanly is polite but never worth failing a completed power operation over.</summary>
    private static async Task TryLogoutAsync(
        ExpectEngine engine, MpTypeDefinition type, ScriptVariables vars, CancellationToken ct)
    {
        if (type.Logout.Count == 0) return;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            await engine.RunAsync(type.Logout, vars, type.Timeouts, cts.Token);
        }
        catch { /* the session is about to be torn down anyway */ }
    }
}
