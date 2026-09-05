using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SysCmd.Simulator;

/// <summary>
/// Base for the fake telnet devices. Two behaviours matter for testing the real code: the listener
/// only binds while the device has power, and it accepts exactly one session at a time — both true
/// of real management processors and console-server ports.
/// </summary>
public abstract class SimTelnetServer(string name, int port) : IAsyncDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _sessionInProgress;

    protected string Name { get; } = name;
    public int Port { get; } = port;

    /// <summary>False while the device is unpowered, in which case nothing listens on the port.</summary>
    protected abstract bool IsPowered { get; }

    /// <summary>
    /// Whether this device serves more than one session at a time. An HP Integrity MP does; an
    /// ALOM and a console-server port do not, and drop the second caller.
    /// </summary>
    protected virtual bool AllowsConcurrentSessions => false;

    /// <summary>Drive one client session to completion.</summary>
    protected abstract Task RunSessionAsync(SimTelnetConnection conn, CancellationToken ct);

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => SuperviseAsync(_cts.Token));
    }

    /// <summary>Bind and unbind the socket as the device gains and loses power.</summary>
    private async Task SuperviseAsync(CancellationToken ct)
    {
        Task? accepting = null;

        while (!ct.IsCancellationRequested)
        {
            if (IsPowered && _listener is null)
            {
                _listener = new TcpListener(IPAddress.Loopback, Port);
                _listener.Start();
                Console.WriteLine($"[sim] {Name} listening on 127.0.0.1:{Port}");
                accepting = Task.Run(() => AcceptAsync(_listener, ct), ct);
            }
            else if (!IsPowered && _listener is not null)
            {
                _listener.Stop();
                _listener = null;
                Console.WriteLine($"[sim] {Name} went dark on port {Port}");
                if (accepting is not null) { try { await accepting; } catch { /* expected */ } }
                accepting = null;
            }

            try { await Task.Delay(500, ct); } catch (OperationCanceledException) { break; }
        }

        _listener?.Stop();
    }

    private async Task AcceptAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch { break; }

            // Single-session devices drop the second caller, exactly like a terminal server.
            if (!AllowsConcurrentSessions && Interlocked.CompareExchange(ref _sessionInProgress, 1, 0) != 0)
            {
                Console.WriteLine($"[sim] {Name} refused a second session");
                try
                {
                    await client.GetStream().WriteAsync(
                        Encoding.ASCII.GetBytes("\r\nPort is already in use.\r\n"), ct);
                }
                catch { /* the caller may already be gone */ }
                client.Dispose();
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    using (client)
                    await using (var conn = new SimTelnetConnection(client))
                        await RunSessionAsync(conn, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.WriteLine($"[sim] {Name} session ended: {ex.Message}");
                }
                finally
                {
                    if (!AllowsConcurrentSessions) Interlocked.Exchange(ref _sessionInProgress, 0);
                }
            }, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        _listener?.Stop();
        if (_loop is not null) { try { await _loop; } catch { /* shutting down */ } }
        _cts?.Dispose();
    }
}

/// <summary>Line-oriented helper over one accepted socket.</summary>
public sealed class SimTelnetConnection(TcpClient client) : IAsyncDisposable
{
    private readonly NetworkStream _stream = client.GetStream();
    private readonly byte[] _buffer = new byte[1024];
    private readonly StringBuilder _pending = new();

    public async Task SendAsync(string text, CancellationToken ct)
        => await _stream.WriteAsync(Encoding.ASCII.GetBytes(text.Replace("\n", "\r\n")), ct);

    /// <summary>
    /// Read one line of input. Characters arrive as the caller types or sends them, so this
    /// accumulates until it sees a carriage return, ignoring any telnet negotiation.
    /// </summary>
    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            var newline = FindNewline();
            if (newline >= 0)
            {
                var line = _pending.ToString(0, newline);
                _pending.Remove(0, newline + 1);
                return line.Trim('\r', '\n', '\0');
            }

            var read = await _stream.ReadAsync(_buffer, ct);
            if (read == 0) return null;

            for (var i = 0; i < read; i++)
            {
                var b = _buffer[i];
                if (b == 255) { i += 2; continue; }        // skip IAC + command + option
                if (b == 0) continue;
                _pending.Append((char)b);
            }
        }
    }

    private int FindNewline()
    {
        for (var i = 0; i < _pending.Length; i++)
            if (_pending[i] is '\r' or '\n') return i;
        return -1;
    }

    public async ValueTask DisposeAsync() => await _stream.DisposeAsync();
}
