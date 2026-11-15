using System.Net.Sockets;

namespace SysCmd.Core.Mp;

/// <summary>
/// A host name that could not be turned into an address, so no connection was attempted. Kept
/// apart from a refused or timed-out connection because the fix is somewhere else entirely: the
/// device is not at fault, the resolver syscmd is using is.
/// </summary>
public sealed class HostLookupException(string host, SocketException inner)
    : Exception(Describe(host, inner), inner)
{
    public string Host { get; } = host;

    private static string Describe(string host, SocketException ex) => ex.SocketErrorCode switch
    {
        // EAI_AGAIN: the resolver got no usable answer, which is a resolver problem, not a name problem.
        SocketError.TryAgain =>
            $"Could not look up '{host}': no DNS server answered ({ex.Message}). " +
            "Check the DNS settings of the host or container syscmd runs in.",

        // EAI_NONAME: a server answered, and the name does not exist there.
        SocketError.HostNotFound =>
            $"Could not look up '{host}': no such host is known. " +
            "Check the name in the config, or the DNS search domain if it is a short name.",

        SocketError.NoData =>
            $"Could not look up '{host}': the name exists but has no address.",

        _ => $"Could not look up '{host}': {ex.Message}",
    };
}
