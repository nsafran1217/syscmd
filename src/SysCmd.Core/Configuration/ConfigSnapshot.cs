using System.Collections.Immutable;

namespace SysCmd.Core.Configuration;

public enum ConfigIssueSeverity { Warning, Error }

/// <summary>A validation or load problem, tied to the file that caused it.</summary>
public sealed record ConfigIssue(
    ConfigIssueSeverity Severity,
    string File,
    string Message);

/// <summary>
/// One immutable read of the whole config directory. Services hold a snapshot for the duration of
/// an operation so a mid-flight reload can never change the world underneath them.
/// </summary>
public sealed class ConfigSnapshot
{
    public required AppConfig App { get; init; }
    public required ImmutableDictionary<string, PduTypeDefinition> PduTypes { get; init; }
    public required ImmutableDictionary<string, MpTypeDefinition> MpTypes { get; init; }
    public required ImmutableDictionary<string, PduConfig> Pdus { get; init; }
    public required ImmutableDictionary<string, ConsoleServerConfig> ConsoleServers { get; init; }
    public required ImmutableDictionary<string, MachineConfig> Machines { get; init; }
    public required ImmutableArray<GroupConfig> Groups { get; init; }
    public required ImmutableArray<ConfigIssue> Issues { get; init; }

    public DateTimeOffset LoadedAt { get; init; } = DateTimeOffset.Now;

    public bool HasErrors => Issues.Any(i => i.Severity == ConfigIssueSeverity.Error);

    public static ConfigSnapshot Empty { get; } = new()
    {
        App = new AppConfig(),
        PduTypes = ImmutableDictionary<string, PduTypeDefinition>.Empty,
        MpTypes = ImmutableDictionary<string, MpTypeDefinition>.Empty,
        Pdus = ImmutableDictionary<string, PduConfig>.Empty,
        ConsoleServers = ImmutableDictionary<string, ConsoleServerConfig>.Empty,
        Machines = ImmutableDictionary<string, MachineConfig>.Empty,
        Groups = [],
        Issues = [],
    };

    public MachineConfig? Machine(string id) => Machines.GetValueOrDefault(id);
    public PduConfig? Pdu(string id) => Pdus.GetValueOrDefault(id);
    public GroupConfig? Group(string id) => Groups.FirstOrDefault(g => g.Id == id);

    /// <summary>The type definition backing a PDU instance, or null if the type is missing.</summary>
    public PduTypeDefinition? TypeOf(PduConfig pdu) => Lookup(PduTypes, pdu.Type);

    /// <summary>
    /// The mp-type driving a machine's management processor: null when it has no MP, or when the
    /// type it names does not exist. Every caller wants the same three-step lookup, so it lives
    /// here rather than being written out at each one.
    /// </summary>
    public MpTypeDefinition? MpTypeFor(MachineConfig machine)
        => machine.Mp is { } mp ? MpTypeFor(mp) : null;

    public MpTypeDefinition? MpTypeFor(MachineMpConfig mp) => Lookup(MpTypes, mp.Type);

    /// <summary>
    /// A type id read from a hand-edited file can be missing entirely - `type:` with nothing after
    /// it - and an empty key is not something a dictionary will look up for you.
    /// </summary>
    private static T? Lookup<T>(ImmutableDictionary<string, T> types, string id) where T : class
        => string.IsNullOrWhiteSpace(id) ? null : types.GetValueOrDefault(id);

    /// <summary>The machine wired to a given outlet, if any.</summary>
    public MachineConfig? MachineOnOutlet(string pduId, int outlet) => Machines.Values
        .FirstOrDefault(m => m.Pdu is { } p && p.Id == pduId && p.Outlet == outlet);
}
