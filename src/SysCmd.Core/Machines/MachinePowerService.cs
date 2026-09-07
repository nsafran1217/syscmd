using SysCmd.Core.Configuration;
using SysCmd.Core.Events;
using SysCmd.Core.Jobs;
using SysCmd.Core.Mp;
using SysCmd.Core.Pdu;

namespace SysCmd.Core.Machines;

/// <summary>
/// The safety logic: never cut power to a running machine, and never assume a machine came up
/// just because its outlet did. Callers enqueue work here rather than driving the PDU directly;
/// <see cref="PduService"/> stays the raw, unguarded layer underneath.
/// </summary>
public sealed class MachinePowerService(
    ConfigStore config,
    PduService pdus,
    IEnumerable<IMpDriver> drivers,
    JobQueue jobs,
    MachineLocks locks,
    EventLog events)
{
    private readonly IMpDriver[] _drivers = [.. drivers];

    // ------------------------------------------------------------- enqueueing

    /// <summary>Queue a power operation for a machine. Returns immediately with a job to watch.</summary>
    public Job EnqueueMachinePower(string machineId, PowerAction action, bool force)
    {
        var machine = config.Current.Machine(machineId)
            ?? throw new InvalidOperationException($"Unknown machine '{machineId}'.");

        // Refused here rather than inside the job, so a caller is told immediately instead of
        // watching a queued job fail a second later for a reason the configuration already knew.
        // This is the same reading of the config the GUI greys its buttons out from.
        var caps = MachineCapabilities.For(config.Current, machine);
        switch (action)
        {
            case PowerAction.On when !caps.CanPowerOn:
                throw new InvalidOperationException(
                    $"{machine.DisplayName} has no outlet and no management processor that can start it.");

            case PowerAction.Off when !caps.CanPowerOff:
                throw new InvalidOperationException(
                    $"{machine.DisplayName} has no outlet and no management processor that can shut it down.");

            case PowerAction.Reset or PowerAction.Reboot when !caps.CanReset:
                throw new InvalidOperationException(caps.HasMp
                    ? $"{MpName(config.Current, machine)} has no reset task, so " +
                      $"{machine.DisplayName} cannot be reset from here. Power cycle its outlet instead."
                    : $"{machine.DisplayName} has no management processor, so it cannot be reset. " +
                      "Power cycle its outlet instead.");
        }

        var verb = action switch
        {
            PowerAction.On => "Power on",
            PowerAction.Off => "Power off",
            _ => "Reset",
        };
        var title = $"{verb} {machine.DisplayName}{(force ? " (forced)" : "")}";
        var target = machine.Pdu is { } b ? $"{b.Id}:{b.Outlet}" : machine.Id;

        return jobs.Enqueue(JobKind.MachinePower, title, target,
            (job, ct) => RunMachinePowerAsync(machineId, action, force, job, ct),
            machineId: machineId, forced: force);
    }

    /// <summary>
    /// Queue an outlet operation.
    ///
    /// Switching an outlet <em>on</em> is a plain outlet operation: applying mains power harms
    /// nothing, and bringing the system itself up through its management processor is a separate,
    /// deliberate action. Switching an outlet <em>off</em> is the dangerous direction, so when a
    /// machine with an MP is bound to the outlet this becomes the full orchestrated shutdown
    /// unless the caller explicitly forces it.
    /// </summary>
    public Job EnqueueOutlet(string pduId, int outlet, PowerAction action, bool force)
    {
        var snapshot = config.Current;
        if (snapshot.Pdu(pduId) is not { } pdu)
            throw new InvalidOperationException($"Unknown PDU '{pduId}'.");

        var machine = snapshot.MachineOnOutlet(pduId, outlet);
        if (machine is { Mp: not null } && !force && action == PowerAction.Off)
            return EnqueueMachinePower(machine.Id, PowerAction.Off, force: false);

        var label = machine?.DisplayName ?? $"{pdu.DisplayName} outlet {outlet}";
        var title = $"{action} {label} (outlet only)";

        return jobs.Enqueue(JobKind.OutletControl, title, $"{pduId}:{outlet}",
            async (job, ct) =>
            {
                // Only worth warning about when power is being taken away from a machine that
                // could have been asked to shut down first.
                if (machine is { Mp: not null } && action is PowerAction.Off or PowerAction.Reboot)
                    events.Warn("power",
                        $"Outlet {action} on {machine.DisplayName} without asking its management processor.",
                        machine.Id, job.Id);

                job.Report($"Switching {pdu.DisplayName} outlet {outlet} {action}");
                await pdus.SetOutletAsync(pduId, outlet, action, ct);
                job.Report("Outlet switched");
            },
            machineId: machine?.Id, forced: force);
    }

    /// <summary>Queue a group operation; members become child jobs, staggered to spread inrush.</summary>
    public Job EnqueueGroupPower(string groupId, PowerAction action, bool force)
    {
        var group = config.Current.Group(groupId)
            ?? throw new InvalidOperationException($"Unknown group '{groupId}'.");

        var title = $"{(action == PowerAction.On ? "Power on" : "Power off")} group {group.DisplayName}";

        return jobs.Enqueue(JobKind.GroupPower, title, groupId,
            (job, ct) => RunGroupAsync(groupId, action, force, job, ct), forced: force);
    }

    // ------------------------------------------------------------- machine run

    private async Task RunMachinePowerAsync(
        string machineId, PowerAction action, bool force, Job job, CancellationToken ct)
    {
        using var _ = await locks.AcquireAsync(machineId, ct);

        var snapshot = config.Current;
        var machine = snapshot.Machine(machineId)
            ?? throw new InvalidOperationException($"Machine '{machineId}' disappeared from the config.");

        switch (action)
        {
            case PowerAction.On: await PowerOnAsync(snapshot, machine, force, job, ct); break;
            case PowerAction.Off: await PowerOffAsync(snapshot, machine, force, job, ct); break;
            case PowerAction.Reset: await ResetAsync(snapshot, machine, job, ct); break;
            default: throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    /// <summary>
    /// Apply outlet power, wait for the management processor to answer, then tell it to power the
    /// system on. Machines whose MP restores its last power state are handled by checking status
    /// first rather than blindly issuing another power-on.
    /// </summary>
    private async Task PowerOnAsync(
        ConfigSnapshot snapshot, MachineConfig machine, bool force, Job job, CancellationToken ct)
    {
        var opts = snapshot.App.Orchestration;

        if (machine.Pdu is { } bind)
        {
            var state = await pdus.ReadOutletAsync(bind.Id, bind.Outlet, ct);
            if (state == PowerState.On)
            {
                job.Report("Outlet is already on");
            }
            else
            {
                job.Report($"Switching outlet {bind.Outlet} on");
                await pdus.SetOutletAsync(bind.Id, bind.Outlet, PowerAction.On, ct);
                await Task.Delay(TimeSpan.FromSeconds(opts.OutletSettleSeconds), ct);
            }
        }

        if (machine.Mp is null)
        {
            job.Report("No management processor configured; outlet power is all we control");
            return;
        }

        if (force)
        {
            job.Report("Forced: skipping the management processor");
            events.Warn("power", $"Forced power on for {machine.DisplayName}; MP sequence skipped.", machine.Id, job.Id);
            return;
        }

        var type = snapshot.MpTypeFor(machine)
            ?? throw new InvalidOperationException($"MP type '{machine.Mp.Type}' is not defined.");

        // Nothing to send. Some machines start themselves the moment mains power returns, and an
        // mp-type with no poweron task is how that is written down; either way the outlet is on
        // and this is as far as syscmd can take it.
        if (!type.CanPowerOn)
        {
            job.Report($"{type.DisplayName} has no power-on task; outlet power is all we control");
            return;
        }

        if (EndpointResolver.ForMp(snapshot, machine) is not { } endpoint)
            throw new InvalidOperationException($"Could not resolve an address for {machine.DisplayName}'s MP.");

        if (!type.ReportsPowerState)
        {
            // With no status task there is nothing to probe with, so the power-on sequence is its
            // own probe: retried only while it fails before sending anything, which is exactly the
            // window where the MP has not finished coming up yet.
            job.Report($"Waiting for the MP at {endpoint} to accept the power-on sequence");
            var sent = await SendUntilAcceptedAsync(
                snapshot, machine, MpTasks.PowerOn, opts.PowerOnMpTimeoutSeconds, job, ct);

            if (!sent.Success)
                throw new InvalidOperationException(
                    $"The power-on sequence failed: {sent.Error}. The outlet is on; check the MP by hand.");

            job.Report($"Power-on sent. {type.DisplayName} cannot report power state, so it is unconfirmed.");
            return;
        }

        job.Report($"Waiting for the MP at {endpoint} to answer");
        var status = await WaitForMpAsync(snapshot, machine, endpoint, opts, job, ct);

        if (!status.Success)
            throw new TimeoutException(
                $"The MP at {endpoint} did not answer within {opts.PowerOnMpTimeoutSeconds}s " +
                $"({status.Error}). The outlet is on; check the MP by hand.");

        if (status.State == PowerState.On)
        {
            job.Report("The MP reports the system is already running");
            return;
        }

        job.Report("Sending the power-on sequence");
        var result = await RunTaskAsync(snapshot, machine, MpTasks.PowerOn, job, ct);
        if (!result.Success)
            throw new InvalidOperationException($"The power-on sequence failed: {result.Error}");

        // Confirm rather than trust: several MPs accept the command and then refuse to act.
        var confirm = await RunTaskAsync(snapshot, machine, MpTasks.Status, job, ct);
        job.Report(confirm.State == PowerState.On
            ? "Confirmed: the system is powered on"
            : $"Power-on sent, but the MP still reports {confirm.State}");
    }

    /// <summary>
    /// Ask the machine to shut down, wait for its MP to confirm it really is off, and only then
    /// cut the outlet. On timeout the job fails with the outlet still live — that is the point.
    /// </summary>
    private async Task PowerOffAsync(
        ConfigSnapshot snapshot, MachineConfig machine, bool force, Job job, CancellationToken ct)
    {
        var opts = snapshot.App.Orchestration;

        if (machine.Mp is not null && !force)
        {
            var type = snapshot.MpTypeFor(machine)
                ?? throw new InvalidOperationException($"MP type '{machine.Mp.Type}' is not defined.");

            if (!type.CanPowerOff)
            {
                // There is no shutdown to ask for, so the outlet is the whole operation - the same
                // thing this would do if the machine had no management processor at all. Warned
                // about rather than refused: power is being taken from a system that was never told
                // to stop, which is worth a line in the log even though it is what was asked for.
                if (machine.Pdu is null)
                    throw new InvalidOperationException(
                        $"{type.DisplayName} has no power-off task and {machine.DisplayName} has no outlet, " +
                        "so there is nothing to switch off.");

                job.Report($"{type.DisplayName} has no power-off task; switching the outlet off directly");
                events.Warn("power",
                    $"{machine.DisplayName}'s outlet was switched off without a shutdown: " +
                    $"{type.DisplayName} has no power-off task.", machine.Id, job.Id);
            }
            else if (type.ReportsPowerState)
            {
                await AskAndConfirmOffAsync(snapshot, machine, type, opts, job, ct);
            }
            else
            {
                await ShutDownUnconfirmedAsync(snapshot, machine, type, job, ct);
            }
        }
        else if (force && machine.Mp is not null)
        {
            job.Report("Forced: cutting the outlet without asking the MP");
            events.Warn("power",
                $"Forced power off for {machine.DisplayName}; the system was not confirmed shut down first.",
                machine.Id, job.Id);
        }

        if (machine.Pdu is not { } bind)
        {
            job.Report("No PDU outlet configured; nothing further to switch");
            return;
        }

        job.Report($"Switching outlet {bind.Outlet} off");
        await pdus.SetOutletAsync(bind.Id, bind.Outlet, PowerAction.Off, ct);
        job.Report("Outlet switched off");
    }

    /// <summary>
    /// The full sequence, for an MP that can both shut the system down and say what state it is in:
    /// ask, then watch until it agrees it is off. On timeout the outlet is left live - that is the
    /// point of the whole design.
    /// </summary>
    private async Task AskAndConfirmOffAsync(
        ConfigSnapshot snapshot, MachineConfig machine, MpTypeDefinition type,
        OrchestrationConfig opts, Job job, CancellationToken ct)
    {
        var status = await RunTaskAsync(snapshot, machine, MpTasks.Status, job, ct);

        if (!status.Success)
            throw new InvalidOperationException(
                $"Could not read power state from {machine.DisplayName}'s MP ({status.Error}). " +
                "The outlet has been left on; use Force Off to override.");

        if (status.State != PowerState.On)
        {
            job.Report($"The MP reports the system is already {status.State}");
            return;
        }

        job.Report("Sending the power-off sequence");
        var result = await RunTaskAsync(snapshot, machine, MpTasks.PowerOff, job, ct);
        if (!result.Success)
            throw new InvalidOperationException(
                $"The power-off sequence failed: {result.Error}. The outlet has been left on.");

        if (!await WaitForOffAsync(snapshot, machine, opts, job, ct))
            throw new TimeoutException(
                $"{machine.DisplayName} did not confirm it was off within {opts.PowerOffConfirmTimeoutSeconds}s. " +
                "The outlet has been left on; use Force Off to override.");
    }

    /// <summary>
    /// Shutting down an MP that cannot be asked how it is getting on. The command still goes out,
    /// but nothing confirms it, so by default the outlet is left live and the job fails saying so -
    /// the same answer a confirmation timeout gives, for the same reason. An mp-type that sets
    /// blindShutdownSeconds trades that for a fixed wait, which is a decision recorded in the
    /// hardware's own file and logged every time it is acted on.
    /// </summary>
    private async Task ShutDownUnconfirmedAsync(
        ConfigSnapshot snapshot, MachineConfig machine, MpTypeDefinition type, Job job, CancellationToken ct)
    {
        job.Report("Sending the power-off sequence");
        var result = await RunTaskAsync(snapshot, machine, MpTasks.PowerOff, job, ct);
        if (!result.Success)
            throw new InvalidOperationException(
                $"The power-off sequence failed: {result.Error}. The outlet has been left on.");

        if (type.BlindShutdownSeconds is not { } grace)
            throw new InvalidOperationException(
                $"{machine.DisplayName} was asked to shut down, but {type.DisplayName} cannot report power " +
                "state, so nothing confirms it did. The outlet has been left on; use Force Off once " +
                "it is down, or set blindShutdownSeconds on the mp-type to cut power after a fixed wait.");

        job.Report($"Waiting {grace}s for {machine.DisplayName} to shut down; nothing here can confirm it");
        await Task.Delay(TimeSpan.FromSeconds(grace), ct);

        events.Warn("power",
            $"Switching {machine.DisplayName}'s outlet off after {type.DisplayName}'s {grace}s blind shutdown " +
            "window. The shutdown was never confirmed.", machine.Id, job.Id);
    }

    private async Task ResetAsync(ConfigSnapshot snapshot, MachineConfig machine, Job job, CancellationToken ct)
    {
        if (machine.Mp is null)
            throw new InvalidOperationException(
                $"{machine.DisplayName} has no management processor, so it cannot be reset. " +
                "Use an outlet power cycle instead.");

        job.Report("Sending the reset sequence");
        var result = await RunTaskAsync(snapshot, machine, MpTasks.Reset, job, ct);
        if (!result.Success) throw new InvalidOperationException($"The reset sequence failed: {result.Error}");
        job.Report("Reset sent");
    }

    // --------------------------------------------------------------- group run

    private async Task RunGroupAsync(string groupId, PowerAction action, bool force, Job parent, CancellationToken ct)
    {
        var group = config.Current.Group(groupId)
            ?? throw new InvalidOperationException($"Unknown group '{groupId}'.");

        // Power on in the configured order; power off in reverse, so dependencies stay up longest.
        var members = action == PowerAction.On ? group.Machines : group.Machines.AsEnumerable().Reverse().ToList();

        var children = new List<Job>();
        var first = true;
        foreach (var machineId in members)
        {
            if (config.Current.Machine(machineId) is null)
            {
                parent.Report($"Skipping unknown machine '{machineId}'");
                continue;
            }

            if (!first && group.StaggerSeconds > 0)
            {
                parent.Report($"Waiting {group.StaggerSeconds}s before the next machine");
                await Task.Delay(TimeSpan.FromSeconds(group.StaggerSeconds), ct);
            }
            first = false;

            var child = jobs.Enqueue(JobKind.MachinePower,
                $"{(action == PowerAction.On ? "Power on" : "Power off")} {config.Current.Machine(machineId)!.DisplayName}",
                machineId,
                (job, innerCt) => RunMachinePowerAsync(machineId, action, force, job, innerCt),
                machineId: machineId, forced: force, parentJobId: parent.Id);

            children.Add(child);
            parent.Report($"Queued {machineId}");
        }

        parent.Report($"Waiting for {children.Count} machines");
        var lastReported = -1;
        while (children.Any(c => !c.IsComplete))
        {
            await Task.Delay(500, ct);

            // Only report when the tally actually moves, or the progress list fills with noise.
            var done = children.Count(c => c.IsComplete);
            if (done != lastReported)
            {
                lastReported = done;
                if (done < children.Count) parent.Report($"{done}/{children.Count} complete");
            }
        }

        var failed = children.Where(c => c.Status == JobStatus.Failed).ToList();
        if (failed.Count > 0)
            throw new InvalidOperationException(
                $"{failed.Count} of {children.Count} machines failed: " +
                string.Join("; ", failed.Select(f => $"{f.Target} ({f.Error})")));

        parent.Report($"All {children.Count} machines complete");
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>How many times to re-attempt a task that failed before issuing any command.</summary>
    private const int TransientRetries = 3;

    /// <summary>
    /// What to call a machine's mp-type in a message, when the message is reached before the type
    /// itself has been resolved - a machine can name a type that no longer exists.
    /// </summary>
    private static string MpName(ConfigSnapshot snapshot, MachineConfig machine)
        => snapshot.MpTypeFor(machine)?.DisplayName ?? "Its MP type";

    private async Task<MpResult> RunTaskAsync(
        ConfigSnapshot snapshot, MachineConfig machine, string task, Job job, CancellationToken ct)
    {
        var type = snapshot.MpTypeFor(machine)
            ?? throw new InvalidOperationException($"MP type '{machine.Mp!.Type}' is not defined.");

        var driver = _drivers.FirstOrDefault(d => d.CanHandle(type))
            ?? throw new InvalidOperationException($"No driver can handle MP transport '{type.Transport}'.");

        MpResult result;
        for (var attempt = 1; ; attempt++)
        {
            result = await driver.RunTaskAsync(snapshot, machine, task, job.Report, ct);

            // Only connect-and-login failures are retried. A service processor that has just
            // dropped a session often refuses the next one for a second or two; failing the whole
            // power operation over that would be wrong. A failure part-way through a task is
            // never retried, because the command may already have been accepted.
            if (result.Success || !result.Transient || attempt >= TransientRetries) return result;

            job.Report($"MP did not accept the session ({result.Error}); retrying");
            await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
        }
    }

    /// <summary>
    /// Retry the MP's status task until it answers. Deliberately not a bare TCP probe: a console
    /// server port accepts exactly one session, so opening a socket just to see whether it is
    /// listening would consume the very session the next step needs. Running the real task is also
    /// a better test - it proves the MP is answering commands, not merely that a port is open.
    /// </summary>
    private async Task<MpResult> WaitForMpAsync(
        ConfigSnapshot snapshot, MachineConfig machine, NetworkEndpoint endpoint,
        OrchestrationConfig opts, Job job, CancellationToken ct)
    {
        var deadline = DateTimeOffset.Now + TimeSpan.FromSeconds(opts.PowerOnMpTimeoutSeconds);
        MpResult result;
        var attempt = 0;

        while (true)
        {
            result = await RunTaskAsync(snapshot, machine, MpTasks.Status, job, ct);
            if (result.Success) return result;

            var remaining = deadline - DateTimeOffset.Now;
            if (remaining <= TimeSpan.Zero) return result;

            if (++attempt % 3 == 0)
                job.Report($"Still waiting for {endpoint} ({remaining.TotalSeconds:F0}s left)");

            await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, remaining.TotalSeconds)), ct);
        }
    }

    /// <summary>
    /// Keep offering a task to an MP that is still coming up, until it takes it or the deadline
    /// passes. Only a transient result - one where nothing was sent - is retried, which is what
    /// makes this safe to use with a power command rather than a status read: a sequence that got
    /// part way through is never repeated.
    /// </summary>
    private async Task<MpResult> SendUntilAcceptedAsync(
        ConfigSnapshot snapshot, MachineConfig machine, string task, int timeoutSeconds,
        Job job, CancellationToken ct)
    {
        var deadline = DateTimeOffset.Now + TimeSpan.FromSeconds(timeoutSeconds);
        var attempt = 0;

        while (true)
        {
            var result = await RunTaskAsync(snapshot, machine, task, job, ct);
            if (result.Success || !result.Transient) return result;

            var remaining = deadline - DateTimeOffset.Now;
            if (remaining <= TimeSpan.Zero) return result;

            if (++attempt % 3 == 0)
                job.Report($"Still waiting for the MP ({remaining.TotalSeconds:F0}s left)");

            await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, remaining.TotalSeconds)), ct);
        }
    }

    /// <summary>Poll the MP's status task until it reports off, or we run out of patience.</summary>
    private async Task<bool> WaitForOffAsync(
        ConfigSnapshot snapshot, MachineConfig machine, OrchestrationConfig opts, Job job, CancellationToken ct)
    {
        var deadline = DateTimeOffset.Now + TimeSpan.FromSeconds(opts.PowerOffConfirmTimeoutSeconds);

        while (DateTimeOffset.Now < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(opts.PowerOffPollIntervalSeconds), ct);

            var status = await RunTaskAsync(snapshot, machine, MpTasks.Status, job, ct);
            if (status.Success && status.State == PowerState.Off)
            {
                job.Report("Confirmed: the system is powered off");
                return true;
            }

            var remaining = (deadline - DateTimeOffset.Now).TotalSeconds;
            job.Report($"Still shutting down ({Math.Max(remaining, 0):F0}s left)");
        }

        return false;
    }
}
