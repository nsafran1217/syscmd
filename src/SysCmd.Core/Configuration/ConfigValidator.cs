namespace SysCmd.Core.Configuration;

/// <summary>
/// Cross-checks references between config objects. Problems are reported rather than thrown: a
/// machine pointing at a deleted PDU should show as broken in the GUI, not stop the app booting.
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

        if (app.Power.PollIntervalSeconds < 5)
            Warn("app.yaml", "power.pollIntervalSeconds below 5s will hammer the PDUs.");
        if (app.Power.CostPerKwh < 0)
            Error("app.yaml", "power.costPerKwh cannot be negative.");

        foreach (var (id, t) in pduTypes)
        {
            var file = $"pdu-types/{id}.yaml";
            if (string.IsNullOrWhiteSpace(t.Outlets.StateOid)) Error(file, "outlets.stateOid is required.");
            if (string.IsNullOrWhiteSpace(t.Outlets.ControlOid)) Error(file, "outlets.controlOid is required.");
            foreach (var action in new[] { "on", "off" })
                if (!t.Outlets.Commands.ContainsKey(action))
                    Error(file, $"outlets.commands is missing '{action}'.");
            if (t.Outlets.StateMap.Count == 0)
                Error(file, "outlets.stateMap is required to interpret readings.");
            if (t.Snmp.Version is not ("v1" or "v2c"))
                Error(file, $"snmp.version '{t.Snmp.Version}' is not supported (use v1 or v2c).");
        }

        foreach (var (id, t) in mpTypes)
        {
            var file = $"mp-types/{id}.yaml";
            if (!t.Transport.Equals("telnet", StringComparison.OrdinalIgnoreCase))
                Error(file, $"transport '{t.Transport}' is not supported (only telnet).");
            foreach (var task in new[] { "poweron", "poweroff", "status" })
                if (!t.Tasks.ContainsKey(task))
                    Warn(file, $"no '{task}' task defined; that operation will be unavailable.");
            if (t.Tasks.TryGetValue("status", out var status) &&
                !status.Any(s => s.Match is { Count: > 0 }))
                Error(file, "the 'status' task has no step with a match block, so it cannot report power state.");
        }

        foreach (var (id, p) in pdus)
        {
            var file = $"pdus/{id}.yaml";
            if (string.IsNullOrWhiteSpace(p.Host)) Error(file, "host is required.");
            if (p.OutletCount <= 0) Error(file, "outletCount must be greater than zero.");
            if (!pduTypes.ContainsKey(p.Type))
                Error(file, $"type '{p.Type}' has no matching file in pdu-types/.");
        }

        foreach (var (id, m) in machines)
        {
            var file = $"machines/{id}.yaml";
            if (string.IsNullOrWhiteSpace(m.Name)) Warn(file, "name is empty; the id will be shown instead.");

            if (m.Pdu is { } bind)
            {
                if (!pdus.TryGetValue(bind.Id, out var pdu))
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
                if (!mpTypes.ContainsKey(mp.Type))
                    Error(file, $"mp.type '{mp.Type}' has no matching file in mp-types/.");
                if (string.IsNullOrWhiteSpace(mp.Host) && mp.Via is null)
                    Error(file, "mp needs either a host or a via (console server) binding.");
                if (!string.IsNullOrWhiteSpace(mp.Host) && mp.Via is not null)
                    Error(file, "mp cannot set both host and via; pick one.");
                if (mp.Via is { } via) CheckPort(file, "mp.via", via);
            }

            if (m.Serial is { } serial) CheckPort(file, "serial", serial);
        }

        foreach (var g in groups)
        {
            if (string.IsNullOrWhiteSpace(g.Id)) { Error("groups.yaml", "a group is missing an id."); continue; }
            foreach (var member in g.Machines)
                if (!machines.ContainsKey(member))
                    Error("groups.yaml", $"group '{g.Id}' references unknown machine '{member}'.");
        }

        return issues;

        void CheckPort(string file, string field, SerialPortBinding binding)
        {
            if (!consoleServers.TryGetValue(binding.Server, out var cs))
                Error(file, $"{field}.server '{binding.Server}' has no matching file in console-servers/.");
            else if (!cs.Ports.ContainsKey(binding.Port))
                Error(file, $"{field}.port {binding.Port} is not mapped on console server '{binding.Server}'.");
        }
    }
}
