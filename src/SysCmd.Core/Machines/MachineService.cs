using SysCmd.Core.Configuration;
using SysCmd.Core.Jobs;
using SysCmd.Core.Mp;
using SysCmd.Core.Pdu;

namespace SysCmd.Core.Machines;

/// <summary>Joins config, live PDU readings and in-flight jobs into the view the UI and API serve.</summary>
public sealed class MachineService(ConfigStore config, PduService pdus, JobQueue jobs)
{
    public async Task<IReadOnlyList<MachineStatus>> ListAsync(CancellationToken ct)
    {
        var snapshot = config.Current;
        var pduStates = await pdus.ReadAllAsync(ct);
        var byPdu = pduStates.ToDictionary(p => p.PduId, StringComparer.OrdinalIgnoreCase);

        return [.. snapshot.Machines.Values
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m => Build(snapshot, m, byPdu))];
    }

    public async Task<MachineStatus?> GetAsync(string id, CancellationToken ct)
    {
        var snapshot = config.Current;
        if (snapshot.Machine(id) is not { } machine) return null;

        var byPdu = new Dictionary<string, PduStatus>(StringComparer.OrdinalIgnoreCase);
        if (machine.Pdu is { } bind && await pdus.ReadAsync(bind.Id, ct) is { } status)
            byPdu[bind.Id] = status;

        return Build(snapshot, machine, byPdu);
    }

    private MachineStatus Build(
        ConfigSnapshot snapshot, MachineConfig machine, IReadOnlyDictionary<string, PduStatus> byPdu)
    {
        OutletStatus? outlet = null;
        PduStatus? pdu = null;
        if (machine.Pdu is { } bind && byPdu.TryGetValue(bind.Id, out pdu))
            outlet = pdu.Outlets.FirstOrDefault(o => o.Outlet == bind.Outlet);

        var job = jobs.ActiveForMachine(machine.Id);

        return new MachineStatus(machine.Id, string.IsNullOrWhiteSpace(machine.Name) ? machine.Id : machine.Name)
        {
            Description = machine.Description,
            Tags = machine.Tags,
            OutletState = outlet?.State ?? PowerState.Unknown,
            PduId = machine.Pdu?.Id,
            PduName = pdu?.Name,
            Outlet = machine.Pdu?.Outlet,
            Watts = outlet?.Watts,
            MpType = machine.Mp?.Type,
            MpAddress = EndpointResolver.ForMp(snapshot, machine)?.ToString(),
            SerialAddress = EndpointResolver.ForSerial(snapshot, machine)?.ToString(),
            Addresses = machine.Addresses,
            ActiveJobId = job?.Id,
            ActiveJobStep = job?.CurrentStep,
        };
    }
}
