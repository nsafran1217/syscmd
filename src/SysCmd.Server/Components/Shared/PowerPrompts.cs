using SysCmd.Core.Machines;

namespace SysCmd.Server.Components.Shared;

/// <summary>
/// What the power controls say about themselves. The machine list and a console window's Power
/// menu offer the same operations on the same machine, so the sentence an operator reads before
/// power is taken away is written once here rather than twice, slightly differently.
///
/// Which controls are offered at all comes from <see cref="MachineCapabilities"/>; this only puts
/// words to it.
/// </summary>
public static class PowerPrompts
{
    public static string OnTooltip(MachineCapabilities caps, string? mpTypeName) =>
        !caps.CanPowerOn ? "Nothing to power on: no outlet and no management processor"
        : !caps.HasMp ? "Switch the outlet on; there is no management processor to bring the system up with"
        : caps.MpPowerOn ? "Apply outlet power and bring the system up through its management processor"
        : $"{Name(mpTypeName)} has no power-on task; this applies outlet power only";

    public static string OffTooltip(MachineCapabilities caps, string? mpTypeName) =>
        !caps.CanPowerOff ? "Nothing to power off: no outlet and no management processor"
        : !caps.HasMp ? "Switch the outlet off"
        : !caps.MpPowerOff
            ? $"{Name(mpTypeName)} has no power-off task; this switches the outlet off without " +
              "shutting the machine down"
        : caps.MpReportsPowerState ? "Shut the system down, then switch its outlet off"
        : $"{Name(mpTypeName)} cannot confirm a shutdown, so the outlet is only switched off if " +
          "the mp-type sets blindShutdownSeconds";

    public static string ResetTooltip(MachineCapabilities caps, string? mpTypeName) =>
        !caps.HasMp ? "No management processor"
        : caps.CanReset ? "Reset through the management processor"
        : $"{Name(mpTypeName)} has no reset task";

    /// <summary>
    /// The are-you-sure message. The three cases really do differ in what is about to happen to the
    /// machine, and this is the last thing anyone reads before it does.
    /// </summary>
    public static string OffConfirm(MachineCapabilities caps, string machineName, string? mpTypeName) =>
        !caps.HasMp
            ? $"Switch {machineName} off? It has no management processor, so its outlet is cut " +
              "without a shutdown."
            : !caps.ShutsDownFirst
                ? $"{Name(mpTypeName)} has no power-off task, so this switches {machineName}'s " +
                  "outlet off without shutting it down first. Continue?"
                : caps.MpReportsPowerState
                    ? $"Shut {machineName} down and switch its outlet off? " +
                      "The outlet stays on if it never confirms it is down."
                    : $"Shut {machineName} down, then switch its outlet off? " +
                      $"Nothing on {Name(mpTypeName)} can confirm it went down.";

    private static string Name(string? mpTypeName) =>
        string.IsNullOrWhiteSpace(mpTypeName) ? "This machine's MP" : mpTypeName;
}
