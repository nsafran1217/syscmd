using System.Text;
using System.Text.RegularExpressions;
using SysCmd.Core.Configuration;

namespace SysCmd.Core.Mp;

/// <summary>Raised when an expected prompt never arrives. Carries the transcript so it can be diagnosed.</summary>
public sealed class ExpectTimeoutException(string pattern, TimeSpan waited, string transcript)
    : Exception($"Timed out after {waited.TotalSeconds:F0}s waiting for \"{pattern}\".")
{
    public string Pattern { get; } = pattern;
    public string Transcript { get; } = transcript;
}

/// <summary>
/// Runs expect/send scripts over a telnet session. This is what turns a YAML step list like
/// cm / pc / on / y into an actual power-on, and it is the only place that understands the script
/// format — adding a management processor type never means touching C#.
/// </summary>
public sealed class ExpectEngine(TelnetSession session)
{
    private readonly StringBuilder _transcript = new();
    private readonly StringBuilder _pending = new();
    private readonly byte[] _buffer = new byte[4096];

    /// <summary>Everything seen and sent so far, for the event log when something goes wrong.</summary>
    public string Transcript => _transcript.ToString();

    /// <summary>
    /// The text consumed by the most recent successful expect. A status step matches against this,
    /// so "wait for the prompt, then read what the command printed on the way there" works.
    /// </summary>
    public string LastWindow { get; private set; } = "";

    /// <summary>
    /// Run a list of steps. Returns the captured power state if any step's match block fired,
    /// which is how the "status" task reports whether a machine is running.
    /// </summary>
    public async Task<PowerState> RunAsync(
        IEnumerable<ExpectStep> steps, ScriptVariables vars, MpTimeouts timeouts, CancellationToken ct)
    {
        var detected = PowerState.Unknown;

        foreach (var step in steps)
        {
            var timeout = TimeSpan.FromSeconds(step.TimeoutSeconds ?? timeouts.ExpectSeconds);

            if (!string.IsNullOrEmpty(step.Expect))
            {
                try
                {
                    await ExpectAsync(step.Expect, timeout, ct);
                }
                catch (ExpectTimeoutException) when (step.Optional)
                {
                    _transcript.Append($"\n[optional step skipped: never saw \"{step.Expect}\"]\n");
                }
            }

            // Match runs on the text this step waited through, so a status probe reads the output
            // of the command sent by the previous step.
            if (step.Match is { Count: > 0 } && DetectState(step.Match, LastWindow) is { } state)
                detected = state;

            if (step.Send is not null)
            {
                var payload = Escapes.Expand(step.Send, vars);
                if (!step.NoNewline) payload += "\r";
                _transcript.Append($"\n>>> {step.Send}\n");
                await session.WriteAsync(payload, ct);
            }

            if (step.DelayMs is { } delay and > 0) await Task.Delay(delay, ct);
        }

        return detected;
    }

    /// <summary>Read until <paramref name="pattern"/> appears, or throw with the transcript attached.</summary>
    public async Task ExpectAsync(string pattern, TimeSpan timeout, CancellationToken ct)
    {
        var matcher = Matcher.Compile(pattern);
        var deadline = DateTimeOffset.Now + timeout;

        // The pattern may already be sitting in text we read while waiting for a previous prompt.
        if (matcher.IsMatch(_pending.ToString())) { Consume(); return; }

        while (DateTimeOffset.Now < deadline)
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(deadline - DateTimeOffset.Now);

            int read;
            try
            {
                read = await session.ReadAsync(_buffer, readCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                break; // the read timed out; fall through to the timeout below
            }

            if (read == 0)
                throw new IOException($"The connection closed while waiting for \"{pattern}\".");

            var text = Encoding.ASCII.GetString(_buffer, 0, read);
            _transcript.Append(text);
            _pending.Append(text);

            if (matcher.IsMatch(_pending.ToString()))
            {
                Consume();
                return;
            }

            // Long-running consoles can produce a lot of output; keep the match window bounded.
            if (_pending.Length > 16384) _pending.Remove(0, _pending.Length - 8192);
        }

        throw new ExpectTimeoutException(pattern, timeout, Transcript);
    }

    /// <summary>Hand the pending buffer to <see cref="LastWindow"/> and start collecting afresh.</summary>
    private void Consume()
    {
        LastWindow = _pending.ToString();
        _pending.Clear();
    }

    /// <summary>Drain whatever the peer has already sent, without waiting for a prompt.</summary>
    public async Task DrainAsync(TimeSpan window, CancellationToken ct)
    {
        var deadline = DateTimeOffset.Now + window;
        while (DateTimeOffset.Now < deadline)
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(deadline - DateTimeOffset.Now);
            try
            {
                var read = await session.ReadAsync(_buffer, readCts.Token);
                if (read == 0) return;
                var text = Encoding.ASCII.GetString(_buffer, 0, read);
                _transcript.Append(text);
                _pending.Append(text);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return; }
        }
    }

    private static PowerState? DetectState(Dictionary<string, string> match, string text)
    {
        foreach (var (key, pattern) in match)
        {
            if (!Matcher.Compile(pattern).IsMatch(text)) continue;
            return key.Trim().ToLowerInvariant() switch
            {
                "on" => PowerState.On,
                "off" => PowerState.Off,
                _ => PowerState.Unknown,
            };
        }
        return null;
    }
}

/// <summary>Values substituted into a script's send strings.</summary>
public sealed record ScriptVariables(string? Username, string? Password);

/// <summary>
/// A compiled expect pattern. Plain text matches as a case-insensitive substring; wrapping the
/// pattern in slashes (/^MP&gt;/i) opts into a regex for the cases where that is not enough.
/// </summary>
public readonly struct Matcher
{
    private readonly string? _literal;
    private readonly Regex? _regex;

    private Matcher(string? literal, Regex? regex) { _literal = literal; _regex = regex; }

    public static Matcher Compile(string pattern)
    {
        if (pattern.Length > 1 && pattern[0] == '/')
        {
            var close = pattern.LastIndexOf('/');
            if (close > 0)
            {
                var body = pattern[1..close];
                var flags = pattern[(close + 1)..];
                var options = RegexOptions.None;
                if (flags.Contains('i')) options |= RegexOptions.IgnoreCase;
                if (flags.Contains('m')) options |= RegexOptions.Multiline;
                if (flags.Contains('s')) options |= RegexOptions.Singleline;
                return new Matcher(null, new Regex(body, options));
            }
        }
        return new Matcher(pattern, null);
    }

    public bool IsMatch(string text) => _regex is not null
        ? _regex.IsMatch(text)
        : text.Contains(_literal!, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Expands the escape syntax used in a script's send strings.</summary>
public static class Escapes
{
    public static string Expand(string input, ScriptVariables vars)
    {
        var sb = new StringBuilder(input.Length);

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];

            // Control characters written as ^A .. ^_ , which covers ^] for the telnet escape.
            if (c == '^' && i + 1 < input.Length)
            {
                var next = char.ToUpperInvariant(input[i + 1]);
                if (next is >= '@' and <= '_') { sb.Append((char)(next - '@')); i++; continue; }
            }

            if (c != '\\' || i + 1 >= input.Length) { sb.Append(c); continue; }

            var esc = input[++i];
            switch (esc)
            {
                case 'r': sb.Append('\r'); break;
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case '0': sb.Append('\0'); break;
                case 'e': sb.Append('\x1b'); break;
                case '\\': sb.Append('\\'); break;
                case '^': sb.Append('^'); break;
                case 'x' when i + 2 < input.Length && byte.TryParse(input.Substring(i + 1, 2),
                        System.Globalization.NumberStyles.HexNumber, null, out var hex):
                    sb.Append((char)hex);
                    i += 2;
                    break;
                default: sb.Append('\\').Append(esc); break;
            }
        }

        return sb.ToString()
            .Replace("{username}", vars.Username ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{password}", vars.Password ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
