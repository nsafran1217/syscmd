using SysCmd.Core.Configuration;

namespace SysCmd.Core.Machines;

/// <summary>
/// What can be done to one machine, worked out from its configuration alone: which tasks its
/// mp-type defines, and whether it is on an outlet. This is the single answer the machine list and
/// the console window's Power menu both grey themselves out from, so the two cannot drift apart and
/// neither has to reason about the orchestration rules itself.
///
/// It says nothing about the machine's current state - no hardware is read to build it - only about
/// what the operator can ask for at all.
/// </summary>
public sealed record MachineCapabilities(
    bool HasMp,
    bool HasOutlet,
    bool MpPowerOn,
    bool MpPowerOff,
    bool MpReset,
    bool MpReportsPowerState)
{
    /// <summary>A machine with nothing wired to it: no MP, no outlet, nothing to ask for.</summary>
    public static MachineCapabilities None { get; } = new(false, false, false, false, false, false);

    /// <summary>
    /// Applying power. The outlet alone is enough - switching it on is a plain, harmless write, and
    /// bringing the system up through its MP afterwards is the part that needs a poweron task.
    /// </summary>
    public bool CanPowerOn => HasOutlet || MpPowerOn;

    /// <summary>
    /// Taking power away. An mp-type with no poweroff task still leaves the outlet, which is then
    /// switched off directly, so this is true wherever there is anything at all to switch off.
    /// </summary>
    public bool CanPowerOff => HasOutlet || MpPowerOff;

    /// <summary>Reset only ever goes through the MP; cycling an outlet is a different, blunter thing.</summary>
    public bool CanReset => MpReset;

    /// <summary>
    /// Whether powering off will ask the machine to shut down first. False means the outlet is cut
    /// without a shutdown - worth saying out loud before it happens.
    /// </summary>
    public bool ShutsDownFirst => MpPowerOff;

    public static MachineCapabilities For(ConfigSnapshot snapshot, MachineConfig machine)
        => For(machine, snapshot.MpTypeFor(machine));

    /// <summary>
    /// The same reading from the parts, for the config validator - which checks files before there
    /// is a snapshot to look anything up in.
    /// </summary>
    public static MachineCapabilities For(MachineConfig machine, MpTypeDefinition? type)
    {
        return new MachineCapabilities(
            HasMp: machine.Mp is not null,
            HasOutlet: machine.Pdu is not null,
            MpPowerOn: type?.CanPowerOn ?? false,
            MpPowerOff: type?.CanPowerOff ?? false,
            MpReset: type?.CanReset ?? false,
            MpReportsPowerState: type?.ReportsPowerState ?? false);
    }

    /// <summary>Convenience for callers that only have an id, e.g. a console window.</summary>
    public static MachineCapabilities For(ConfigSnapshot snapshot, string machineId)
        => snapshot.Machine(machineId) is { } machine ? For(snapshot, machine) : None;
}
