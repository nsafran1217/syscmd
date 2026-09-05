using System.Collections.Concurrent;
using SysCmd.Core.Configuration;
using SysCmd.Core.Events;

namespace SysCmd.Core.Pdu;

/// <summary>
/// Reads and controls outlets, translating between our model and whatever OIDs and magic numbers
/// a given PDU type uses. Readings are cached briefly so the dashboard re-rendering does not turn
/// into an SNMP flood.
/// </summary>
public sealed class PduService(ConfigStore config, SnmpPduClient snmp, EventLog events)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(3);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, PduStatus Status)> _cache = new();

    /// <summary>Read every configured PDU, in parallel.</summary>
    public async Task<IReadOnlyList<PduStatus>> ReadAllAsync(CancellationToken ct, bool fresh = false)
    {
        var snapshot = config.Current;
        var tasks = snapshot.Pdus.Values.Select(p => ReadAsync(p.Id, ct, fresh));
        var results = await Task.WhenAll(tasks);
        return [.. results.OfType<PduStatus>().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<PduStatus?> ReadAsync(string pduId, CancellationToken ct, bool fresh = false)
    {
        var snapshot = config.Current;
        if (snapshot.Pdu(pduId) is not { } pdu) return null;
        if (snapshot.TypeOf(pdu) is not { } type)
            return Unreachable(pdu, $"PDU type '{pdu.Type}' is not defined.");

        if (!fresh && _cache.TryGetValue(pduId, out var cached) && DateTimeOffset.Now - cached.At < CacheTtl)
            return cached.Status;

        try
        {
            var status = await ReadUncachedAsync(snapshot, pdu, type, ct);
            _cache[pduId] = (DateTimeOffset.Now, status);
            return status;
        }
        catch (Exception ex)
        {
            var status = Unreachable(pdu, ex.Message);
            _cache[pduId] = (DateTimeOffset.Now, status);
            return status;
        }
    }

    private async Task<PduStatus> ReadUncachedAsync(
        ConfigSnapshot snapshot, PduConfig pdu, PduTypeDefinition type, CancellationToken ct)
    {
        var outlets = Enumerable.Range(1, pdu.OutletCount).ToList();

        // One request for every outlet state, plus the load OID and any per-outlet metering.
        var oids = outlets.Select(n => SnmpPduClient.ResolveOid(type.Outlets.StateOid, n)).ToList();
        if (type.Power?.LoadOid is { Length: > 0 } loadOid) oids.Add(loadOid);
        if (type.Power?.PerOutletWattsOid is { Length: > 0 } wattsOid)
            oids.AddRange(outlets.Select(n => SnmpPduClient.ResolveOid(wattsOid, n)));

        var values = await snmp.GetIntsAsync(pdu, type, oids, ct);

        var rows = new List<OutletStatus>(pdu.OutletCount);
        foreach (var n in outlets)
        {
            var raw = values.GetValueOrDefault(SnmpPduClient.ResolveOid(type.Outlets.StateOid, n), int.MinValue);
            var state = MapState(type, raw);
            var machine = snapshot.MachineOnOutlet(pdu.Id, n);

            double? watts = null;
            if (type.Power?.PerOutletWattsOid is { Length: > 0 } w &&
                values.TryGetValue(SnmpPduClient.ResolveOid(w, n), out var raw2))
                watts = Normalise(
                    raw2,
                    type.Power.PerOutletUnit ?? type.Power.LoadUnit,
                    type.Power.NominalVolts).Watts;

            rows.Add(new OutletStatus(pdu.Id, n, state)
            {
                MachineId = machine?.Id,
                MachineName = machine?.Name,
                Watts = watts,
            });
        }

        double? pduWatts = null, pduAmps = null, pduVolts = null;
        if (type.Power?.LoadOid is { Length: > 0 } lo && values.TryGetValue(lo, out var rawLoad))
        {
            var reading = Normalise(rawLoad, type.Power.LoadUnit, type.Power.NominalVolts);
            pduWatts = reading.Watts;
            pduAmps = reading.Amps;
            pduVolts = type.Power.NominalVolts;
        }

        return new PduStatus(pdu.Id, pdu.Name, Reachable: true, rows)
        {
            Watts = pduWatts,
            Amps = pduAmps,
            Volts = pduVolts,
        };
    }

    private static PduStatus Unreachable(PduConfig pdu, string error) =>
        new(pdu.Id, pdu.Name, Reachable: false,
            [.. Enumerable.Range(1, Math.Max(pdu.OutletCount, 0))
                .Select(n => new OutletStatus(pdu.Id, n, PowerState.Unknown))])
        { Error = error };

    /// <summary>Read one outlet's state without pulling the whole PDU.</summary>
    public async Task<PowerState> ReadOutletAsync(string pduId, int outlet, CancellationToken ct)
    {
        var snapshot = config.Current;
        if (snapshot.Pdu(pduId) is not { } pdu || snapshot.TypeOf(pdu) is not { } type)
            return PowerState.Unknown;

        var oid = SnmpPduClient.ResolveOid(type.Outlets.StateOid, outlet);
        var values = await snmp.GetIntsAsync(pdu, type, [oid], ct);
        return MapState(type, values.GetValueOrDefault(oid, int.MinValue));
    }

    /// <summary>
    /// Switch an outlet. This is the raw SNMP write with no safety logic — orchestration lives in
    /// MachinePowerService, which is what the UI normally calls.
    /// </summary>
    public async Task SetOutletAsync(string pduId, int outlet, PowerAction action, CancellationToken ct)
    {
        var snapshot = config.Current;
        var pdu = snapshot.Pdu(pduId) ?? throw new InvalidOperationException($"Unknown PDU '{pduId}'.");
        var type = snapshot.TypeOf(pdu) ?? throw new InvalidOperationException($"PDU type '{pdu.Type}' is not defined.");

        if (outlet < 1 || outlet > pdu.OutletCount)
            throw new ArgumentOutOfRangeException(nameof(outlet), $"Outlet {outlet} is outside 1..{pdu.OutletCount}.");

        var key = action switch
        {
            PowerAction.On => "on",
            PowerAction.Off => "off",
            PowerAction.Reboot or PowerAction.Reset => "reboot",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

        if (!type.Outlets.Commands.TryGetValue(key, out var value))
            throw new InvalidOperationException($"PDU type '{pdu.Type}' does not define an outlet command for '{key}'.");

        var oid = SnmpPduClient.ResolveOid(type.Outlets.ControlOid, outlet);
        await snmp.SetIntAsync(pdu, type, oid, value, ct);

        _cache.TryRemove(pduId, out _);
        events.Info("pdu", $"Set {pdu.Name} outlet {outlet} → {key}", snapshot.MachineOnOutlet(pduId, outlet)?.Id);
    }

    private static PowerState MapState(PduTypeDefinition type, int raw)
    {
        if (raw == int.MinValue) return PowerState.Unknown;
        if (!type.Outlets.StateMap.TryGetValue(raw, out var name)) return PowerState.Unknown;
        return name.Trim().ToLowerInvariant() switch
        {
            "on" => PowerState.On,
            "off" => PowerState.Off,
            _ => PowerState.Unknown,
        };
    }

    /// <summary>Convert a raw reading into both watts and amps, whichever the PDU actually reported.</summary>
    internal static (double Watts, double Amps) Normalise(int raw, LoadUnit unit, double volts)
    {
        if (volts <= 0) volts = 120;
        return unit switch
        {
            LoadUnit.Watts => (raw, raw / volts),
            LoadUnit.DeciWatts => (raw / 10.0, raw / 10.0 / volts),
            LoadUnit.Amps => (raw * volts, raw),
            LoadUnit.DeciAmps => (raw / 10.0 * volts, raw / 10.0),
            _ => (raw, raw / volts),
        };
    }
}
