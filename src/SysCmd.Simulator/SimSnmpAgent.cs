using System.Net;
using System.Net.Sockets;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;

namespace SysCmd.Simulator;

/// <summary>
/// A minimal SNMP v1/v2c agent serving an APC-shaped OID tree. Enough to answer the GETs and SETs
/// the real PduService issues, so outlet control and power tracking can be exercised with no
/// hardware present.
/// </summary>
public sealed class SimSnmpAgent(SimLab lab, int port) : IAsyncDisposable
{
    // The same OIDs the shipped apc-ap7900 pdu-type points at.
    private const string OutletStateBase = "1.3.6.1.4.1.318.1.1.4.4.2.1.3.";
    private const string OutletNameBase = "1.3.6.1.4.1.318.1.1.4.4.2.1.4.";
    private const string LoadOid = "1.3.6.1.4.1.318.1.1.12.2.3.1.1.2.1";
    // rPDUIdentDevicePowerWatts, as the AP89xx family reports it: whole watts.
    private const string WattsOid = "1.3.6.1.4.1.318.1.1.12.1.16.0";

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public void Start()
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => ServeAsync(_cts.Token));
        Console.WriteLine($"[sim] SNMP agent listening on 127.0.0.1:{port} ({lab.OutletCount} outlets)");
    }

    private async Task ServeAsync(CancellationToken ct)
    {
        var registry = new UserRegistry();

        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try { packet = await _udp!.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception) { continue; }

            try
            {
                var messages = MessageFactory.ParseMessages(packet.Buffer, 0, packet.Buffer.Length, registry);
                foreach (var message in messages)
                {
                    if (Respond(message) is not { } response) continue;
                    var bytes = response.ToBytes();
                    await _udp.SendAsync(bytes, bytes.Length, packet.RemoteEndPoint);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[sim] snmp parse error: {ex.Message}");
            }
        }
    }

    private ISnmpMessage? Respond(ISnmpMessage request)
    {
        var pdu = request.Pdu();
        var community = request.Parameters.UserName;

        var variables = new List<Variable>();

        switch (pdu.TypeCode)
        {
            case SnmpType.GetRequestPdu:
                foreach (var v in pdu.Variables)
                    variables.Add(new Variable(v.Id, Read(v.Id.ToString())));
                break;

            case SnmpType.SetRequestPdu:
                foreach (var v in pdu.Variables)
                {
                    Write(v.Id.ToString(), v.Data);
                    variables.Add(new Variable(v.Id, Read(v.Id.ToString())));
                }
                break;

            default:
                return null;
        }

        return new ResponseMessage(
            request.RequestId(), request.Version, community, ErrorCode.NoError, 0, variables);
    }

    private ISnmpData Read(string oid)
    {
        lab.Tick();

        if (oid.StartsWith(OutletStateBase, StringComparison.Ordinal) &&
            int.TryParse(oid[OutletStateBase.Length..], out var stateOutlet))
            return new Integer32(lab.IsOutletOn(stateOutlet) ? 1 : 2);

        if (oid.StartsWith(OutletNameBase, StringComparison.Ordinal) &&
            int.TryParse(oid[OutletNameBase.Length..], out var nameOutlet) &&
            nameOutlet >= 1 && nameOutlet <= lab.OutletCount)
            return new OctetString(lab.OutletNames[nameOutlet]);

        if (oid == WattsOid) return new Gauge32((uint)Math.Round(lab.TotalWatts()));

        if (oid == LoadOid)
        {
            // The real AP7900 reports whole-unit current in tenths of an amp.
            var amps = lab.TotalWatts() / 120.0;
            return new Gauge32((uint)Math.Round(amps * 10));
        }

        return new NoSuchInstance();
    }

    private void Write(string oid, ISnmpData data)
    {
        if (!oid.StartsWith(OutletStateBase, StringComparison.Ordinal)) return;
        if (!int.TryParse(oid[OutletStateBase.Length..], out var outlet)) return;
        if (data is not Integer32 value) return;

        switch (value.ToInt32())
        {
            case 1: lab.SetOutlet(outlet, true); break;
            case 2: lab.SetOutlet(outlet, false); break;
            case 3:                                        // reboot: off, brief pause, back on
                lab.SetOutlet(outlet, false);
                _ = Task.Run(async () => { await Task.Delay(3000); lab.SetOutlet(outlet, true); });
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        _udp?.Dispose();
        if (_loop is not null) { try { await _loop; } catch { /* shutting down */ } }
        _cts?.Dispose();
    }
}
