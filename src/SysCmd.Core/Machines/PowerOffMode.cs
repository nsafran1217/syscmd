namespace SysCmd.Core.Machines;

/// <summary>
/// How far an unforced power off goes. The machine list switches the whole machine off; a console
/// is a view of the system running on it, so its Power menu stops at the system and leaves the
/// outlet - and the management processor living on it - alone.
/// </summary>
public enum PowerOffMode
{
    /// <summary>Shut the system down through its MP, confirm it, then switch its outlet off.</summary>
    SystemAndOutlet,

    /// <summary>
    /// Shut the system down through its MP and leave the outlet on. A machine with no MP has
    /// nothing but its outlet, which is switched off as usual.
    /// </summary>
    SystemOnly,
}
