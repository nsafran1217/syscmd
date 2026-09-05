namespace SysCmd.Core.Configuration;

/// <summary>Power state of an outlet or a machine, as far as we can observe it.</summary>
public enum PowerState { Unknown, On, Off }

/// <summary>What a caller wants done to an outlet or machine.</summary>
public enum PowerAction { On, Off, Reset, Reboot }

// ---------------------------------------------------------------- app.yaml

public sealed class AppConfig
{
    public SiteConfig Site { get; set; } = new();
    public PowerConfig Power { get; set; } = new();
    public OrchestrationConfig Orchestration { get; set; } = new();
}

public sealed class SiteConfig
{
    public string Name { get; set; } = "Home Lab";

    /// <summary>
    /// The lab's default CDE palette, by the name of a .dp file. A browser that has made its own
    /// choice overrides this; this is what everyone else, and every first visit, gets.
    /// </summary>
    public string Theme { get; set; } = "Default";

    /// <summary>Default backdrop, by the name of a file in the backdrops directory.</summary>
    public string Backdrop { get; set; } = "Toronto";

    /// <summary>
    /// Which colour set tints the backdrop. dtwm cycles workspaces through 3, 5, 6 and 7; with one
    /// workspace there is one choice to make, so it is exposed rather than fixed.
    /// </summary>
    public int BackdropColorSet { get; set; } = 3;

    /// <summary>
    /// Palettes to choose between when a browser asks for a random theme. Empty means every palette
    /// that loaded is fair game.
    /// </summary>
    public List<string> RandomThemes { get; set; } = [];
}

public sealed class PowerConfig
{
    public int PollIntervalSeconds { get; set; } = 30;
    public decimal CostPerKwh { get; set; } = 0.14m;
    public string Currency { get; set; } = "USD";
}

public sealed class OrchestrationConfig
{
    /// <summary>Grace period after switching an outlet on before we start probing the MP.</summary>
    public int OutletSettleSeconds { get; set; } = 5;

    /// <summary>How long to wait for an MP to become reachable after applying outlet power.</summary>
    public int PowerOnMpTimeoutSeconds { get; set; } = 180;

    /// <summary>How long to wait for a machine to confirm it is off before giving up (outlet stays on).</summary>
    public int PowerOffConfirmTimeoutSeconds { get; set; } = 300;

    public int PowerOffPollIntervalSeconds { get; set; } = 10;
}

// ------------------------------------------------------- pdu-types/*.yaml

/// <summary>How a PDU reports its load, so we can normalise everything to watts.</summary>
public enum LoadUnit { Watts, DeciWatts, Amps, DeciAmps }

/// <summary>Reusable driver definition for a model of PDU: which OIDs mean what.</summary>
public sealed class PduTypeDefinition
{
    /// <summary>File stem, e.g. "apc-ap7900". Assigned by the loader, not stored in the file.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public PduSnmpConfig Snmp { get; set; } = new();
    public PduOutletConfig Outlets { get; set; } = new();
    public PduPowerConfig? Power { get; set; }
}

public sealed class PduSnmpConfig
{
    /// <summary>"v1" or "v2c". v3 is not implemented yet but the field is honoured for validation.</summary>
    public string Version { get; set; } = "v2c";
}

public sealed class PduOutletConfig
{
    /// <summary>OID read to learn an outlet's state. "{outlet}" is replaced with the outlet number.</summary>
    public string StateOid { get; set; } = "";

    /// <summary>OID written to control an outlet. Often identical to <see cref="StateOid"/>.</summary>
    public string ControlOid { get; set; } = "";

    /// <summary>Optional OID holding the outlet's name as configured on the PDU itself.</summary>
    public string? NameOid { get; set; }

    /// <summary>Integer written to ControlOid for each action, keyed "on"/"off"/"reboot".</summary>
    public Dictionary<string, int> Commands { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Integer read from StateOid mapped to a state, keyed by the raw value.</summary>
    public Dictionary<int, string> StateMap { get; set; } = new();
}

public sealed class PduPowerConfig
{
    /// <summary>Whole-PDU load OID. No "{outlet}" substitution.</summary>
    public string? LoadOid { get; set; }
    public LoadUnit LoadUnit { get; set; } = LoadUnit.Watts;

    /// <summary>Line voltage assumed when the PDU reports current rather than power.</summary>
    public double NominalVolts { get; set; } = 120;

    /// <summary>Optional per-outlet wattage OID for metered PDUs. Supports "{outlet}".</summary>
    public string? PerOutletWattsOid { get; set; }

    /// <summary>
    /// Unit of <see cref="PerOutletWattsOid"/>. Defaults to <see cref="LoadUnit"/>, but the two
    /// are often different: APC metered rPDUs report the phase load in deciamps while per-outlet
    /// power is in tenths of a watt, so reusing the whole-unit setting would mis-scale it.
    /// </summary>
    public LoadUnit? PerOutletUnit { get; set; }
}

// ------------------------------------------------------------ pdus/*.yaml

/// <summary>A physical PDU on the network, driven by a <see cref="PduTypeDefinition"/>.</summary>
public sealed class PduConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Id of the pdu-type definition that describes this model's OIDs.</summary>
    public string Type { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 161;
    public SnmpCommunity Community { get; set; } = new();
    public int OutletCount { get; set; }
}

public sealed class SnmpCommunity
{
    public string Read { get; set; } = "public";
    public string Write { get; set; } = "private";
}

// --------------------------------------------------------- mp-types/*.yaml

/// <summary>Reusable expect/send script describing how to talk to a model of management processor.</summary>
public sealed class MpTypeDefinition
{
    /// <summary>File stem, e.g. "hp-mp". Assigned by the loader.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Currently only "telnet"; the transport layer is pluggable.</summary>
    public string Transport { get; set; } = "telnet";
    public int DefaultPort { get; set; } = 23;

    /// <summary>Steps run once on connect, before any task.</summary>
    public List<ExpectStep> Login { get; set; } = new();

    /// <summary>The MP's idle prompt. Used to resynchronise before running a task.</summary>
    public string? Prompt { get; set; }

    /// <summary>
    /// Whether this MP accepts more than one session at once. An HP Integrity MP does, so a
    /// console window and a power job can share it; an ALOM does not. Defaults to false, which is
    /// the safe assumption. Ignored when the MP is reached through a console server: that is a
    /// single physical serial wire whatever is on the end of it.
    /// </summary>
    public bool AllowsConcurrentSessions { get; set; }

    public MpTimeouts Timeouts { get; set; } = new();

    /// <summary>Task name ("poweron", "poweroff", "reset", "status") to its step list.</summary>
    public Dictionary<string, List<ExpectStep>> Tasks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Steps run on the way out, best-effort.</summary>
    public List<ExpectStep> Logout { get; set; } = new();
}

public sealed class MpTimeouts
{
    public int ExpectSeconds { get; set; } = 20;
    public int ConnectSeconds { get; set; } = 10;
}

/// <summary>
/// One step of an expect script: optionally wait for <see cref="Expect"/> to appear in the
/// output, then transmit <see cref="Send"/>. <see cref="Match"/> turns a step into a state probe.
/// </summary>
public sealed class ExpectStep
{
    /// <summary>Substring to wait for, or a regex when written as /pattern/ or /pattern/i.</summary>
    public string? Expect { get; set; }

    /// <summary>
    /// Text to transmit. Supports {username}, {password}, \r, \n, \t, \xNN and ^X control escapes.
    /// A carriage return is appended unless the step sets <see cref="NoNewline"/>.
    /// </summary>
    public string? Send { get; set; }

    /// <summary>Suppress the automatic carriage return after <see cref="Send"/>.</summary>
    public bool NoNewline { get; set; }

    /// <summary>Per-step override of the type's expect timeout.</summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>Fixed pause after this step, for MPs that need a moment to settle.</summary>
    public int? DelayMs { get; set; }

    /// <summary>
    /// For status probes: patterns keyed "on"/"off" tested against the output captured by this
    /// step. The first match decides the reported power state.
    /// </summary>
    public Dictionary<string, string>? Match { get; set; }

    /// <summary>Treat a failure to see <see cref="Expect"/> as non-fatal and carry on.</summary>
    public bool Optional { get; set; }
}

// -------------------------------------------------- console-servers/*.yaml

/// <summary>A terminal server that exposes serial ports as TCP ports.</summary>
public sealed class ConsoleServerConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    /// <summary>Physical port number to the TCP port that reaches it.</summary>
    public Dictionary<int, int> Ports { get; set; } = new();
}

// -------------------------------------------------------- machines/*.yaml

public sealed class MachineConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<string> Tags { get; set; } = new();
    public MachinePduBinding? Pdu { get; set; }
    public MachineMpConfig? Mp { get; set; }
    public SerialPortBinding? Serial { get; set; }
    public List<MachineAddress> Addresses { get; set; } = new();
}

public sealed class MachinePduBinding
{
    public string Id { get; set; } = "";
    public int Outlet { get; set; }
}

public sealed class MachineMpConfig
{
    /// <summary>Id of the mp-type definition describing this MP's command language.</summary>
    public string Type { get; set; } = "";

    /// <summary>Direct network address of the MP. Mutually exclusive with <see cref="Via"/>.</summary>
    public string? Host { get; set; }
    public int? Port { get; set; }

    /// <summary>Reach the MP through a console server port instead of a direct address.</summary>
    public SerialPortBinding? Via { get; set; }

    public string? Username { get; set; }
    public string? Password { get; set; }
}

public sealed class SerialPortBinding
{
    /// <summary>Id of the console server.</summary>
    public string Server { get; set; } = "";
    /// <summary>Physical port number on that console server.</summary>
    public int Port { get; set; }
}

public sealed class MachineAddress
{
    public string? Label { get; set; }
    public string? Ip { get; set; }
    public string? Hostname { get; set; }
}

// ------------------------------------------------------------- groups.yaml

public sealed class GroupsFile
{
    public List<GroupConfig> Groups { get; set; } = new();
}

public sealed class GroupConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Machine ids, in the order they should be powered on.</summary>
    public List<string> Machines { get; set; } = new();
    /// <summary>Delay between starting each member, to spread inrush current.</summary>
    public int StaggerSeconds { get; set; } = 10;
}
