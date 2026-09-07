using SysCmd.Core.Configuration;

namespace SysCmd.Core.Machines;

/// <summary>
/// Everything the dashboard needs about one machine: its configured identity, the state of the
/// outlet feeding it, and which consoles it offers.
/// </summary>
public sealed record MachineStatus(string Id, string Name)
{
    public string? Description { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Outlet power, which is what we can observe cheaply. Not the same as the OS being up.</summary>
    public PowerState OutletState { get; init; } = PowerState.Unknown;

    public string? PduId { get; init; }
    public string? PduName { get; init; }
    public int? Outlet { get; init; }
    public double? Watts { get; init; }

    public string? MpType { get; init; }

    /// <summary>The mp-type's display name, for messages an operator reads.</summary>
    public string? MpTypeName { get; init; }

    public string? MpAddress { get; init; }
    public bool HasMpConsole => MpAddress is not null;

    /// <summary>
    /// What can be asked of this machine at all - see <see cref="MachineCapabilities"/>. Service
    /// processors differ, and a machine may have only an outlet, so the UI greys out what is not
    /// on offer rather than presenting an action that could only fail.
    /// </summary>
    public MachineCapabilities Capabilities { get; init; } = MachineCapabilities.None;

    public string? SerialAddress { get; init; }
    public bool HasSerialConsole => SerialAddress is not null;

    public IReadOnlyList<MachineAddress> Addresses { get; init; } = [];

    /// <summary>Id of the job currently acting on this machine, if any. Drives the amber LED.</summary>
    public string? ActiveJobId { get; init; }
    public string? ActiveJobStep { get; init; }
    public bool IsBusy => ActiveJobId is not null;
}
