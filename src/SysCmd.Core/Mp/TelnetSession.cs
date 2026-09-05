using System.Net.Sockets;

namespace SysCmd.Core.Mp;

/// <summary>
/// A telnet connection with just enough option negotiation to keep a vintage MP or a terminal
/// server happy. Negotiation bytes are answered and stripped, so callers — the expect engine and
/// the browser console bridge alike — only ever see payload data.
/// </summary>
public sealed class TelnetSession : IAsyncDisposable
{
    // Telnet protocol constants (RFC 854).
    private const byte IAC = 255, DONT = 254, DO = 253, WONT = 252, WILL = 251, SB = 250, SE = 240;
    private const byte OptBinary = 0, OptEcho = 1, OptSuppressGoAhead = 3;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly byte[] _raw = new byte[8192];
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private TelnetSession(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    public static async Task<TelnetSession> ConnectAsync(string host, int port, TimeSpan connectTimeout, CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(connectTimeout);
            await client.ConnectAsync(host, port, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"Timed out connecting to {host}:{port} after {connectTimeout.TotalSeconds:F0}s.");
        }
        catch
        {
            client.Dispose();
            throw;
        }
        return new TelnetSession(client);
    }

    /// <summary>
    /// Read the next chunk of payload bytes, answering any negotiation seen along the way.
    /// Returns 0 when the peer closed the connection.
    /// </summary>
    public async Task<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
    {
        while (true)
        {
            var read = await _stream.ReadAsync(_raw.AsMemory(0, Math.Min(_raw.Length, destination.Length)), ct);
            if (read == 0) return 0;

            var replies = new List<byte[]>();
            var produced = Filter(read, destination, replies);

            foreach (var reply in replies) await WriteAsync(reply, ct);

            // A chunk that was pure negotiation yields nothing; loop rather than reporting EOF.
            if (produced > 0) return produced;
        }
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(data, ct);
            await _stream.FlushAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public Task WriteAsync(string text, CancellationToken ct)
        => WriteAsync(System.Text.Encoding.ASCII.GetBytes(text), ct);

    private enum ParseState { Data, Iac, Negotiate, Subneg, SubnegIac }

    private ParseState _state = ParseState.Data;
    private byte _pendingCommand;

    /// <summary>
    /// Pull IAC sequences out of the freshly read bytes in <c>_raw</c>, copying payload into
    /// <paramref name="destination"/> and queueing any negotiation replies. The parser keeps its
    /// state in fields, so a sequence split across two reads is still handled correctly.
    /// </summary>
    private int Filter(int count, Memory<byte> destination, List<byte[]> replies)
    {
        var span = destination.Span;
        var produced = 0;

        for (var i = 0; i < count && produced < span.Length; i++)
        {
            var b = _raw[i];
            switch (_state)
            {
                case ParseState.Data:
                    if (b == IAC) _state = ParseState.Iac;
                    else span[produced++] = b;
                    break;

                case ParseState.Iac:
                    switch (b)
                    {
                        case IAC:                                   // escaped literal 0xFF
                            span[produced++] = IAC;
                            _state = ParseState.Data;
                            break;
                        case DO or DONT or WILL or WONT:
                            _pendingCommand = b;
                            _state = ParseState.Negotiate;
                            break;
                        case SB:
                            _state = ParseState.Subneg;
                            break;
                        default:                                    // single-byte command we ignore
                            _state = ParseState.Data;
                            break;
                    }
                    break;

                case ParseState.Negotiate:
                    if (Respond(_pendingCommand, b) is { } reply) replies.Add(reply);
                    _state = ParseState.Data;
                    break;

                case ParseState.Subneg:
                    if (b == IAC) _state = ParseState.SubnegIac;
                    break;

                case ParseState.SubnegIac:
                    // IAC SE ends the subnegotiation; IAC anything-else stays inside it.
                    _state = b == SE ? ParseState.Data : ParseState.Subneg;
                    break;
            }
        }

        return produced;
    }

    /// <summary>
    /// Answer negotiation as a dumb client: agree to suppress-go-ahead and binary, let the far end
    /// echo, and refuse everything else. That is what these MPs expect from a terminal.
    /// </summary>
    private static byte[]? Respond(byte command, byte option)
    {
        byte reply = command switch
        {
            DO => option is OptSuppressGoAhead or OptBinary ? WILL : WONT,
            WILL => option is OptSuppressGoAhead or OptBinary or OptEcho ? DO : DONT,
            // A refusal from the peer needs no reply; answering one would risk a negotiation loop.
            _ => 0,
        };
        return reply == 0 ? null : [IAC, reply, option];
    }

    public async ValueTask DisposeAsync()
    {
        try { await _stream.DisposeAsync(); } catch { /* closing a dead socket is not interesting */ }
        _client.Dispose();
        _writeLock.Dispose();
    }
}
