using System.Net.WebSockets;
using System.Text;
using SysCmd.Core.Configuration;
using SysCmd.Core.Events;
using SysCmd.Core.Mp;

namespace SysCmd.Server.Console;

/// <summary>
/// Pumps bytes between a browser terminal and a telnet session. The endpoint lease is taken for
/// the life of the window, so opening a console genuinely reserves the wire — a power job that
/// needs the same console-server port is told it is busy instead of silently interleaving.
/// </summary>
public sealed class ConsoleBridge(ConfigStore config, EndpointBroker broker, EventLog events)
{
    private static readonly TimeSpan LeaseWait = TimeSpan.FromSeconds(2);

    public async Task HandleAsync(WebSocket socket, string machineId, ConsoleTarget target, CancellationToken ct)
    {
        var snapshot = config.Current;

        if (snapshot.Machine(machineId) is not { } machine)
        {
            await CloseWithMessageAsync(socket, $"Unknown machine '{machineId}'.", ct);
            return;
        }

        if (EndpointResolver.For(snapshot, machine, target) is not { } endpoint)
        {
            await CloseWithMessageAsync(socket,
                $"{machine.Name} has no {(target == ConsoleTarget.Mp ? "management processor" : "serial console")} configured.", ct);
            return;
        }

        IAsyncDisposable lease;
        try
        {
            var exclusive = EndpointResolver.RequiresExclusiveSession(snapshot, machine, target);
            lease = await broker.AcquireAsync(
                endpoint, $"console window ({machine.Name})", LeaseWait, ct, exclusive);
        }
        catch (EndpointBusyException ex)
        {
            await CloseWithMessageAsync(socket, ex.Message + "\r\nClose the other session and try again.", ct);
            return;
        }

        await using (lease)
        {
            TelnetSession session;
            try
            {
                session = await TelnetSession.ConnectAsync(endpoint.Host, endpoint.Port, TimeSpan.FromSeconds(10), ct);
            }
            catch (Exception ex)
            {
                await CloseWithMessageAsync(socket, $"Could not connect to {endpoint}: {ex.Message}", ct);
                return;
            }

            var label = target == ConsoleTarget.Mp ? "management processor" : "serial console";
            events.Info("console", $"Console opened on {machine.Name} ({label}) at {endpoint}", machine.Id);
            await SendTextAsync(socket, $"\x1b[33m*** Connected to {endpoint} - {machine.Name} {label} ***\x1b[0m\r\n", ct);

            await using (session)
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                try
                {
                    // Either direction ending tears down the other, so a dropped telnet session
                    // closes the browser tab's socket rather than leaving it hanging.
                    await Task.WhenAny(
                        PumpDeviceToBrowserAsync(session, socket, linked.Token),
                        PumpBrowserToDeviceAsync(socket, session, linked.Token));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    events.Warn("console", $"Console on {machine.Name} ended: {ex.Message}", machine.Id);
                }
                finally
                {
                    await linked.CancelAsync();
                }
            }

            events.Info("console", $"Console closed on {machine.Name}", machine.Id);
            await CloseAsync(socket, "session ended", ct);
        }
    }

    private static async Task PumpDeviceToBrowserAsync(TelnetSession session, WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var read = await session.ReadAsync(buffer, ct);
            if (read == 0) return;
            await socket.SendAsync(buffer.AsMemory(0, read), WebSocketMessageType.Binary, true, ct);
        }
    }

    private static async Task PumpBrowserToDeviceAsync(WebSocket socket, TelnetSession session, CancellationToken ct)
    {
        var buffer = new byte[4096];
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return;
            if (result.Count > 0) await session.WriteAsync(buffer.AsMemory(0, result.Count), ct);
        }
    }

    private static async Task SendTextAsync(WebSocket socket, string text, CancellationToken ct)
    {
        if (socket.State != WebSocketState.Open) return;
        await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Binary, true, ct);
    }

    /// <summary>Show the reason in the terminal before closing, so the user sees why it failed.</summary>
    private static async Task CloseWithMessageAsync(WebSocket socket, string message, CancellationToken ct)
    {
        await SendTextAsync(socket, $"\r\n\x1b[31m*** {message} ***\x1b[0m\r\n", ct);
        await CloseAsync(socket, message, ct);
    }

    private static async Task CloseAsync(WebSocket socket, string reason, CancellationToken ct)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        try
        {
            // Close reasons are capped at 123 bytes by the protocol.
            var text = reason.Length > 100 ? reason[..100] : reason;
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, text, ct);
        }
        catch { /* the peer may already be gone */ }
    }
}
