using System.Net;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using SysCmd.Core.Configuration;

namespace SysCmd.Core.Pdu;

/// <summary>
/// Thin SNMP wrapper over SharpSnmpLib. Knows nothing about outlets — it just gets and sets
/// integers at OIDs, which is all any of the PDUs in the lab actually need.
/// </summary>
public sealed class SnmpPduClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Bound an SNMP operation by cancelling it rather than by abandoning it. SharpSnmpLib waits on
    /// its own UDP socket with no timeout of its own, so giving up with Task.WaitAsync leaves that
    /// socket open and the operation pending for the life of the process - which is the state a PDU
    /// on a switched-off breaker leaves behind every single poll, and it accumulates.
    /// </summary>
    private static CancellationTokenSource Deadline(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        return cts;
    }

    /// <summary>
    /// Our own deadline elapsing is a timeout, not a cancellation, and reads better as one in the
    /// "unreachable" line the GUI shows. A cancel that came from the caller is passed through.
    /// Fully qualified: SharpSnmpLib has a TimeoutException of its own, which this is not.
    /// </summary>
    private static System.TimeoutException Timedout(PduConfig pdu)
        => new($"{pdu.Host}:{pdu.Port} did not answer within {Timeout.TotalSeconds:F0}s.");

    /// <summary>Substitute the outlet number into a template OID from a pdu-type definition.</summary>
    public static string ResolveOid(string template, int outlet)
        => template.Replace("{outlet}", outlet.ToString(), StringComparison.Ordinal);

    private static VersionCode Version(PduConfig pdu, PduTypeDefinition type)
        => type.Snmp.Version.Equals("v1", StringComparison.OrdinalIgnoreCase) ? VersionCode.V1 : VersionCode.V2;

    private static IPEndPoint Endpoint(PduConfig pdu)
    {
        // Accept either a literal address or a DNS name, since lab PDUs are often named.
        if (!IPAddress.TryParse(pdu.Host, out var ip))
        {
            var resolved = Dns.GetHostAddresses(pdu.Host);
            ip = resolved.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                 ?? resolved.FirstOrDefault()
                 ?? throw new InvalidOperationException($"Could not resolve PDU host '{pdu.Host}'.");
        }
        return new IPEndPoint(ip, pdu.Port);
    }

    /// <summary>Read several OIDs in one request. Missing values come back absent from the result.</summary>
    public async Task<IReadOnlyDictionary<string, int>> GetIntsAsync(
        PduConfig pdu, PduTypeDefinition type, IEnumerable<string> oids, CancellationToken ct)
    {
        var list = oids.Distinct().ToList();
        if (list.Count == 0) return new Dictionary<string, int>();

        var variables = list.Select(o => new Variable(new ObjectIdentifier(o))).ToList();

        IList<Variable> result;
        using (var cts = Deadline(ct))
        {
            try
            {
                result = await Messenger.GetAsync(
                    Version(pdu, type),
                    Endpoint(pdu),
                    new OctetString(pdu.Community.Read),
                    variables,
                    cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Timedout(pdu); }
        }

        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var v in result)
        {
            if (v.Data.TypeCode is SnmpType.NoSuchInstance or SnmpType.NoSuchObject or SnmpType.Null) continue;
            if (TryToInt(v.Data, out var value)) map[v.Id.ToString()] = value;
        }
        return map;
    }

    /// <summary>Read a single OID as a string, for outlet name lookups.</summary>
    public async Task<string?> GetStringAsync(
        PduConfig pdu, PduTypeDefinition type, string oid, CancellationToken ct)
    {
        IList<Variable> result;
        using (var cts = Deadline(ct))
        {
            try
            {
                result = await Messenger.GetAsync(
                    Version(pdu, type),
                    Endpoint(pdu),
                    new OctetString(pdu.Community.Read),
                    [new Variable(new ObjectIdentifier(oid))],
                    cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Timedout(pdu); }
        }

        var data = result.FirstOrDefault()?.Data;
        if (data is null || data.TypeCode is SnmpType.NoSuchInstance or SnmpType.NoSuchObject or SnmpType.Null)
            return null;
        return data.ToString();
    }

    /// <summary>Write an integer, using the write community.</summary>
    public async Task SetIntAsync(
        PduConfig pdu, PduTypeDefinition type, string oid, int value, CancellationToken ct)
    {
        using var cts = Deadline(ct);
        try
        {
            await Messenger.SetAsync(
                Version(pdu, type),
                Endpoint(pdu),
                new OctetString(pdu.Community.Write),
                [new Variable(new ObjectIdentifier(oid), new Integer32(value))],
                cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Timedout(pdu); }
    }

    /// <summary>PDUs return their readings as Integer32, Gauge32, Counter32 or TimeTicks depending on the model.</summary>
    private static bool TryToInt(ISnmpData data, out int value)
    {
        switch (data)
        {
            case Integer32 i: value = i.ToInt32(); return true;
            case Gauge32 g: value = (int)g.ToUInt32(); return true;
            case Counter32 c: value = (int)c.ToUInt32(); return true;
            case TimeTicks t: value = (int)t.ToUInt32(); return true;
            default:
                return int.TryParse(data.ToString(), out value);
        }
    }
}
