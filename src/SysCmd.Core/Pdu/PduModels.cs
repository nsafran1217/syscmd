using SysCmd.Core.Configuration;

namespace SysCmd.Core.Pdu;

/// <summary>A single outlet as last read from the PDU, joined with whatever machine claims it.</summary>
public sealed record OutletStatus(
    string PduId,
    int Outlet,
    PowerState State)
{
    /// <summary>Outlet label configured on the PDU itself, when the type exposes a name OID.</summary>
    public string? PduLabel { get; init; }

    /// <summary>Id of the machine bound to this outlet in our config, if any.</summary>
    public string? MachineId { get; init; }
    public string? MachineName { get; init; }

    /// <summary>Per-outlet draw, only for metered PDUs.</summary>
    public double? Watts { get; init; }

    /// <summary>Best label to show on the button.</summary>
    public string DisplayName => MachineName ?? PduLabel ?? $"Outlet {Outlet}";
}

/// <summary>A whole PDU's current state, as served to the dashboard.</summary>
public sealed record PduStatus(
    string PduId,
    string Name,
    bool Reachable,
    IReadOnlyList<OutletStatus> Outlets)
{
    /// <summary>Whole-unit draw in watts, normalised from whatever unit the PDU reports.</summary>
    public double? Watts { get; init; }
    public double? Amps { get; init; }
    public double? Volts { get; init; }

    /// <summary>Why the PDU could not be read, when <see cref="Reachable"/> is false.</summary>
    public string? Error { get; init; }

    public DateTimeOffset ReadAt { get; init; } = DateTimeOffset.Now;

    public int OnCount => Outlets.Count(o => o.State == PowerState.On);
}
