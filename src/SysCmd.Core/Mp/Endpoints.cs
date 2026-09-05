using SysCmd.Core.Configuration;

namespace SysCmd.Core.Mp;

/// <summary>Which of a machine's two possible consoles a caller wants.</summary>
public enum ConsoleTarget { Mp, Serial }

/// <summary>
/// A resolved TCP destination plus the identity used for locking. Two bindings that land on the
/// same host and port share a key, which is exactly what we want: a console server port is a
/// single physical wire no matter how many config objects point at it.
/// </summary>
public sealed record NetworkEndpoint(string Host, int Port, string Description)
{
    public string Key => $"{Host.ToLowerInvariant()}:{Port}";
    public override string ToString() => $"{Host}:{Port}";
}

public static class EndpointResolver
{
    /// <summary>Where to connect to reach a machine's management processor.</summary>
    public static NetworkEndpoint? ForMp(ConfigSnapshot snapshot, MachineConfig machine)
    {
        if (machine.Mp is not { } mp) return null;

        if (!string.IsNullOrWhiteSpace(mp.Host))
        {
            var port = mp.Port ?? snapshot.MpTypes.GetValueOrDefault(mp.Type)?.DefaultPort ?? 23;
            return new NetworkEndpoint(mp.Host, port, $"{machine.Name} management processor");
        }

        if (mp.Via is { } via) return ForSerial(snapshot, via, $"{machine.Name} management processor");
        return null;
    }

    /// <summary>Where to connect to reach a machine's serial console.</summary>
    public static NetworkEndpoint? ForSerial(ConfigSnapshot snapshot, MachineConfig machine)
        => machine.Serial is { } s ? ForSerial(snapshot, s, $"{machine.Name} serial console") : null;

    public static NetworkEndpoint? ForSerial(ConfigSnapshot snapshot, SerialPortBinding binding, string description)
    {
        if (snapshot.ConsoleServers.GetValueOrDefault(binding.Server) is not { } cs) return null;
        if (!cs.Ports.TryGetValue(binding.Port, out var tcpPort)) return null;
        return new NetworkEndpoint(cs.Host, tcpPort, description);
    }

    public static NetworkEndpoint? For(ConfigSnapshot snapshot, MachineConfig machine, ConsoleTarget target)
        => target == ConsoleTarget.Mp ? ForMp(snapshot, machine) : ForSerial(snapshot, machine);

    /// <summary>
    /// Whether a session on this route must be held exclusively. Anything through a console server
    /// is one physical serial wire and always is; a directly addressed MP only is when its type
    /// says it cannot handle concurrent logins.
    /// </summary>
    public static bool RequiresExclusiveSession(
        ConfigSnapshot snapshot, MachineConfig machine, ConsoleTarget target)
    {
        if (target == ConsoleTarget.Serial) return true;
        if (machine.Mp is not { } mp) return true;
        if (mp.Via is not null) return true;
        return snapshot.MpTypes.GetValueOrDefault(mp.Type) is not { AllowsConcurrentSessions: true };
    }
}
