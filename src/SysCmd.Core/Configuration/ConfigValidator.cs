using SysCmd.Core.Machines;

namespace SysCmd.Core.Configuration;

/// <summary>
/// Cross-checks references between config objects. Problems are reported rather than thrown: a
/// machine pointing at a deleted PDU should show as broken in the GUI, not stop the app booting.
/// That promise only holds if the checks themselves cannot throw, so each object is validated
/// inside a guard - an unexpected failure becomes an issue naming the file that caused it.
/// </summary>
public static class ConfigValidator
{
    public static List<ConfigIssue> Validate(
        AppConfig app,
        IReadOnlyDictionary<string, PduTypeDefinition> pduTypes,
        IReadOnlyDictionary<string, MpTypeDefinition> mpTypes,
        IReadOnlyDictionary<string, PduConfig> pdus,
        IReadOnlyDictionary<string, ConsoleServerConfig> consoleServers,
        IReadOnlyDictionary<string, MachineConfig> machines,
        IReadOnlyList<GroupConfig> groups)
    {
        var issues = new List<ConfigIssue>();
        void Error(string file, string msg) => issues.Add(new(ConfigIssueSeverity.Error, file, msg));
        void Warn(string file, string msg) => issues.Add(new(ConfigIssueSeverity.Warning, file, msg));

        // Anything unforeseen in one file is reported against that file and the rest still load.
        // Without this a single surprise - a null where a list was expected, say - would come out
        // of ConfigStore.Load() as an unhandled exception at startup, naming nothing useful.
        void Check(string file, Action check)
        {
            try { check(); }
            catch (Exception ex)
            {
                Error(file, $"could not be checked: {ex.Message}. Fix or remove this file.");
            }
        }

        Check("app.yaml", () =>
        {
            if (app.Power.PollIntervalSeconds < 5)
                Warn("app.yaml", "power.pollIntervalSeconds below 5s will hammer the PDUs.");
            if (app.Power.CostPerKwh < 0)
                Error("app.yaml", "power.costPerKwh cannot be negative.");
        });

        foreach (var (id, t) in pduTypes)
        {
            var file = $"pdu-types/{id}.yaml";
            Check(file, () =>
            {
                if (string.IsNullOrWhiteSpace(t.Outlets.StateOid)) Error(file, "outlets.stateOid is required.");
                if (string.IsNullOrWhiteSpace(t.Outlets.ControlOid)) Error(file, "outlets.controlOid is required.");
                foreach (var action in new[] { "on", "off" })
                    if (!t.Outlets.Commands.ContainsKey(action))
                        Error(file, $"outlets.commands is missing '{action}'.");
                if (t.Outlets.StateMap.Count == 0)
                    Error(file, "outlets.stateMap is required to interpret readings.");
                if (t.Snmp.Version is not ("v1" or "v2c"))
                    Error(file, $"snmp.version '{t.Snmp.Version}' is not supported (use v1 or v2c).");
            });
        }

        foreach (var (id, t) in mpTypes)
        {
            var file = $"mp-types/{id}.yaml";
            Check(file, () => CheckMpType(file, t, Error, Warn));
        }

        foreach (var (id, p) in pdus)
        {
            var file = $"pdus/{id}.yaml";
            Check(file, () =>
            {
                if (string.IsNullOrWhiteSpace(p.Host)) Error(file, "host is required.");
                if (p.OutletCount <= 0) Error(file, "outletCount must be greater than zero.");
                if (string.IsNullOrWhiteSpace(p.Type))
                    Error(file, "type is required; it names a file in pdu-types/.");
                else if (!pduTypes.ContainsKey(p.Type))
                    Error(file, $"type '{p.Type}' has no matching file in pdu-types/.");
            });
        }

        foreach (var (id, m) in machines)
        {
            var file = $"machines/{id}.yaml";
            Check(file, () =>
            {
                if (string.IsNullOrWhiteSpace(m.Name)) Warn(file, "name is empty; the id will be shown instead.");

                if (m.Pdu is { } bind)
                {
                    if (string.IsNullOrWhiteSpace(bind.Id))
                        Error(file, "pdu.id is required; it names a file in pdus/.");
                    else if (!pdus.TryGetValue(bind.Id, out var pdu))
                        Error(file, $"pdu.id '{bind.Id}' has no matching file in pdus/.");
                    else if (bind.Outlet < 1 || bind.Outlet > pdu.OutletCount)
                        Error(file, $"pdu.outlet {bind.Outlet} is outside 1..{pdu.OutletCount} on '{bind.Id}'.");

                    var clash = machines.Values.FirstOrDefault(o =>
                        o.Id != m.Id && o.Pdu is { } ob && ob.Id == bind.Id && ob.Outlet == bind.Outlet);
                    if (clash is not null)
                        Error(file, $"outlet {bind.Id}:{bind.Outlet} is also claimed by machine '{clash.Id}'.");
                }

                if (m.Mp is { } mp)
                {
                    if (string.IsNullOrWhiteSpace(mp.Type))
                        Error(file, "mp.type is required; it names a file in mp-types/.");
                    else if (!mpTypes.TryGetValue(mp.Type, out var mpType))
                        Error(file, $"mp.type '{mp.Type}' has no matching file in mp-types/.");
                    else
                        CheckMachineAgainstMpType(file, m, mpType, Warn);

                    if (string.IsNullOrWhiteSpace(mp.Host) && mp.Via is null)
                        Error(file, "mp needs either a host or a via (console server) binding.");
                    if (!string.IsNullOrWhiteSpace(mp.Host) && mp.Via is not null)
                        Error(file, "mp cannot set both host and via; pick one.");
                    if (mp.Via is { } via) CheckPort(file, "mp.via", via);
                }

                if (m.Serial is { } serial) CheckPort(file, "serial", serial);
            });
        }

        foreach (var g in groups)
        {
            Check("groups.yaml", () =>
            {
                if (string.IsNullOrWhiteSpace(g.Id)) { Error("groups.yaml", "a group is missing an id."); return; }
                foreach (var member in g.Machines)
                    if (string.IsNullOrWhiteSpace(member) || !machines.ContainsKey(member))
                        Error("groups.yaml", $"group '{g.Id}' references unknown machine '{member}'.");
            });
        }

        return issues;

        void CheckPort(string file, string field, SerialPortBinding binding)
        {
            if (string.IsNullOrWhiteSpace(binding.Server))
                Error(file, $"{field}.server is required; it names a file in console-servers/.");
            else if (!consoleServers.TryGetValue(binding.Server, out var cs))
                Error(file, $"{field}.server '{binding.Server}' has no matching file in console-servers/.");
            else if (!cs.Ports.ContainsKey(binding.Port))
                Error(file, $"{field}.port {binding.Port} is not mapped on console server '{binding.Server}'.");
        }
    }

    /// <summary>
    /// An mp-type describes what one model of service processor can do, and models differ: some
    /// will start a machine but never stop it, some cannot say which state it is in. Leaving a task
    /// out is a supported way to say so, so those are notes rather than errors - what is reported
    /// as a fault is a task written down but left empty, which is a file someone stopped editing
    /// half way rather than a decision.
    /// </summary>
    private static void CheckMpType(
        string file, MpTypeDefinition t, Action<string, string> error, Action<string, string> warn)
    {
        if (string.IsNullOrWhiteSpace(t.Transport) ||
            !t.Transport.Equals("telnet", StringComparison.OrdinalIgnoreCase))
            error(file, $"transport '{t.Transport}' is not supported (only telnet).");

        foreach (var (name, steps) in t.Tasks)
        {
            if (steps is not { Count: > 0 })
                error(file, $"task '{name}' has no steps under it. Write the script, or remove the " +
                            "key entirely to declare that this model cannot do it.");
            else if (!MpTasks.All.Contains(name, StringComparer.OrdinalIgnoreCase))
                warn(file, $"task '{name}' is never run; the tasks syscmd uses are " +
                           $"{string.Join(", ", MpTasks.All)}.");
        }

        if (!t.CanPowerOn)
            warn(file, "no 'poweron' task: outlet power can be applied, but syscmd cannot start the system.");
        if (!t.CanPowerOff)
            warn(file, "no 'poweroff' task: powering a machine off switches its outlet off without " +
                       "asking the machine to shut down first.");
        if (!t.CanReset)
            warn(file, "no 'reset' task: the reset action will be unavailable.");

        if (!t.Supports(MpTasks.Status))
            warn(file, "no 'status' task: power state cannot be read, so a shutdown cannot be confirmed. " +
                       "See blindShutdownSeconds if the outlet should still be switched off.");
        else if (!t.ReportsPowerState)
            error(file, "the 'status' task has no step with a match block, so it cannot report power " +
                        "state; it will be treated as if there were no status task at all.");

        if (t.BlindShutdownSeconds is { } grace)
        {
            if (grace <= 0)
                error(file, "blindShutdownSeconds must be greater than zero; remove it to leave the " +
                            "outlet on when a shutdown cannot be confirmed.");
            else if (t.ReportsPowerState)
                warn(file, "blindShutdownSeconds is ignored: this type has a status task, so shutdowns " +
                           "are confirmed rather than timed.");
            else if (!t.CanPowerOff)
                warn(file, "blindShutdownSeconds has nothing to wait for without a 'poweroff' task.");
        }
    }

    /// <summary>
    /// What only the pairing can say. An mp-type's own limits are already reported against its file,
    /// once, and repeating them here for every machine using that type buries the machine's own
    /// problems in copies of a sentence the operator has already read. What is left is the case the
    /// type file cannot know about: an operation that this machine has no other way to perform,
    /// because it has no outlet to fall back on either.
    /// </summary>
    private static void CheckMachineAgainstMpType(
        string file, MachineConfig m, MpTypeDefinition type, Action<string, string> warn)
    {
        var caps = MachineCapabilities.For(m, type);

        if (!caps.CanPowerOn)
            warn(file, $"{type.DisplayName} has no power-on task and this machine has no outlet, " +
                       "so there is no way to power it on.");
        if (!caps.CanPowerOff)
            warn(file, $"{type.DisplayName} has no power-off task and this machine has no outlet, " +
                       "so there is no way to power it off.");
    }
}
