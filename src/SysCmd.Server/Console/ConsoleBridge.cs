using System.Net.WebSockets;
using System.Text;
using SysCmd.Core.Configuration;
using SysCmd.Core.Events;
using SysCmd.Core.Mp;

namespace SysCmd.Server.Console;

/// <summary>
/// Pumps bytes between a browser terminal and a telnet session. The endpoint lease is taken for
/// the life of the window, so opening a console genuinely reserves the wire — a power job that
/// needs the same console-server port is told it is busy instead of silently interleaving.
/// </summary>
public sealed class ConsoleBridge(ConfigStore config, EndpointBroker broker, EventLog events)
{
    private static readonly TimeSpan LeaseWait = TimeSpan.FromSeconds(2);

    public async Task HandleAsync(WebSocket socket, string machineId, ConsoleTarget target, CancellationToken ct)
    {
        var snapshot = config.Current;

        if (snapshot.Machine(machineId) is not { } machine)
        {
            await CloseWithMessageAsync(socket, $"Unknown machine '{machineId}'.", ct);
            return;
        }

        if (EndpointResolver.For(snapshot, machine, target) is not { } endpoint)
        {
            await CloseWithMessageAsync(socket,
                $"{machine.Name} has no {(target == ConsoleTarget.Mp ? "management processor" : "serial console")} configured.", ct);
            return;
        }

        IAsyncDisposable lease;
        try
        {
            var exclusive = EndpointResolver.RequiresExclusiveSession(snapshot, machine, target);
            lease = await broker.AcquireAsync(
                endpoint, $"console window ({machine.Name})", LeaseWait, ct, exclusive);
        }
        catch (EndpointBusyException ex)
        {
            await CloseWithMessageAsync(socket, ex.Message + "\r\nClose the other session and try again.", ct);
            return;
        }

        await using (lease)
        {
            TelnetSession session;
            try
            {
                session = await TelnetSession.ConnectAsync(endpoint.Host, endpoint.Port, TimeSpan.FromSeconds(10), ct);
            }
            catch (Exception ex)
            {
                await CloseWithMessageAsync(socket, $"Could not connect to {endpoint}: {ex.Message}", ct);
                return;
            }

            var label = target == ConsoleTarget.Mp ? "management processor" : "serial console";
            events.Info("console", $"Console opened on {machine.Name} ({label}) at {endpoint}", machine.Id);
            await SendTextAsync(socket, $"\x1b[33m*** Connected to {endpoint} - {machine.Name} {label} ***\x1b[0m\r\n", ct);

            // Shared between the two pumps: the browser asks for a login on the control channel,
            // and the device-to-browser pump is what actually drives it.
            var login = new LoginState(snapshot, machine, target);

            // Which side hangs up is the first thing anyone asks when a session ends by itself,
            // and syscmd never puts a clock on an idle console - the pumps below block until one
            // end stops. So say which one it was: a device-side end means the far end dropped the
            // TCP connection, which for a console-server port is the terminal server's own idle
            // timeout; a browser-side end means the WebSocket went, which is a closed tab, a lost
            // network, or a proxy timing the connection out.
            var endedBy = "the session";

            await using (session)
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                try
                {
                    // Either direction ending tears down the other, so a dropped telnet session
                    // closes the browser tab's socket rather than leaving it hanging.
                    var device = PumpDeviceToBrowserAsync(session, socket, login, linked.Token);
                    var browser = PumpBrowserToDeviceAsync(socket, session, login, events, machine, linked.Token);

                    var first = await Task.WhenAny(device, browser);
                    endedBy = first == device ? $"{endpoint} hung up" : "the browser disconnected";

                    // WhenAny does not rethrow, so a pump that faulted would otherwise be
                    // reported as an ordinary close with no reason attached.
                    await first;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    events.Warn("console", $"Console on {machine.Name} ended: {ex.Message}", machine.Id);
                }
                finally
                {
                    await linked.CancelAsync();
                }
            }

            events.Info("console", $"Console closed on {machine.Name} - {endedBy}", machine.Id);
            await CloseAsync(socket, "session ended", ct);
        }
    }

    private static async Task PumpDeviceToBrowserAsync(
        TelnetSession session, WebSocket socket, LoginState login, CancellationToken ct)
    {
        var buffer = new byte[4096];
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var read = await session.ReadAsync(buffer, ct);
            if (read == 0) return;

            var text = Encoding.ASCII.GetString(buffer, 0, read);
            await socket.SendAsync(buffer.AsMemory(0, read), WebSocketMessageType.Binary, true, ct);

            // Watching the same bytes the browser is being shown keeps a single reader on the
            // session, and lets a login that was already half-answered pick up where it is.
            if (login.Advance(text) is { } reply) await session.WriteAsync(reply, ct);
            if (login.TakeNotice() is { } notice) await SendTextAsync(socket, notice, ct);
        }
    }

    /// <summary>
    /// Keystrokes arrive as binary frames and go straight through. A text frame is a control
    /// message from the window's own buttons, which is how it stays out of the byte stream.
    /// </summary>
    private static async Task PumpBrowserToDeviceAsync(
        WebSocket socket, TelnetSession session, LoginState login,
        EventLog events, MachineConfig machine, CancellationToken ct)
    {
        var buffer = new byte[4096];
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return;
            if (result.Count == 0) continue;

            if (result.MessageType == WebSocketMessageType.Text)
            {
                var command = Encoding.UTF8.GetString(buffer, 0, result.Count).Trim();
                if (command == "login")
                {
                    var (started, message) = login.Start();
                    await SendTextAsync(socket, $"\r\n\x1b[33m*** {message} ***\x1b[0m\r\n", ct);
                    if (started)
                    {
                        events.Info("console", $"Login sequence sent on {machine.Name}'s console", machine.Id);

                        // Answer whatever is already on screen first. The login prompt has
                        // usually arrived long before the operator presses the button, and a
                        // blind carriage return here would be swallowed as the username -
                        // shifting the whole exchange by one and sending the username as the
                        // password.
                        if (login.Advance("") is { } opening)
                            await session.WriteAsync(opening, ct);
                        else if (login.HasSeenNothing)
                            // A console-server port often stays silent until something is typed.
                            await session.WriteAsync("\r", ct);

                        if (login.TakeNotice() is { } settled) await SendTextAsync(socket, settled, ct);
                    }
                }
                continue;
            }

            await session.WriteAsync(buffer.AsMemory(0, result.Count), ct);
        }
    }

    /// <summary>Tracks whether a login replay is running on this session.</summary>
    private sealed class LoginState(ConfigSnapshot snapshot, MachineConfig machine, ConsoleTarget target)
    {
        private readonly StringBuilder _recent = new();
        private LoginAssistant? _assistant;
        private string? _notice;

        /// <summary>True when the device has not said a word yet, so there is nothing to match.</summary>
        public bool HasSeenNothing => _recent.Length == 0;

        /// <summary>Begin a login, or explain why not. Never throws at the operator.</summary>
        public (bool Started, string Message) Start()
        {
            if (target != ConsoleTarget.Mp)
                return (false, "Login is only offered on a management processor console.");

            if (_assistant is { Finished: false })
                return (false, "A login is already in progress.");

            _assistant = LoginAssistant.TryCreate(snapshot, machine, _recent.ToString(), out var error);
            return _assistant is null
                ? (false, error)
                : (true, "Sending the configured login for " + machine.Name);
        }

        /// <summary>Feed device output; returns anything the login wants transmitted.</summary>
        public string? Advance(string fromDevice)
        {
            // Retained so a login started after the prompt has scrolled by still matches it.
            _recent.Append(fromDevice);
            if (_recent.Length > 8192) _recent.Remove(0, _recent.Length - 4096);

            if (_assistant is not { Finished: false } assistant) return null;

            var send = assistant.Observe(fromDevice);

            if (assistant.Finished)
                _notice = assistant.Error is { } err
                    ? $"Login stopped: {err}."
                    : "Login sent.";

            return send;
        }

        /// <summary>One-shot: the message to show the operator, if the login just settled.</summary>
        public string? TakeNotice()
        {
            if (_notice is not { } notice) return null;
            _notice = null;
            return $"\r\n\x1b[33m*** {notice} ***\x1b[0m\r\n";
        }
    }

    private static async Task SendTextAsync(WebSocket socket, string text, CancellationToken ct)
    {
        if (socket.State != WebSocketState.Open) return;
        await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Binary, true, ct);
    }

    /// <summary>Show the reason in the terminal before closing, so the user sees why it failed.</summary>
    private static async Task CloseWithMessageAsync(WebSocket socket, string message, CancellationToken ct)
    {
        await SendTextAsync(socket, $"\r\n\x1b[31m*** {message} ***\x1b[0m\r\n", ct);
        await CloseAsync(socket, message, ct);
    }

    private static async Task CloseAsync(WebSocket socket, string reason, CancellationToken ct)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        try
        {
            // Close reasons are capped at 123 bytes by the protocol.
            var text = reason.Length > 100 ? reason[..100] : reason;
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, text, ct);
        }
        catch { /* the peer may already be gone */ }
    }
}
