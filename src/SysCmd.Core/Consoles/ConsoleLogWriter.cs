using System.Text;
using Microsoft.Extensions.Logging;

namespace SysCmd.Core.Consoles;

/// <summary>
/// One session's log file, open for as long as its console is. Every write is flushed as it
/// arrives, so the log page can follow a session that is still running, and a failure to write
/// stops the log rather than taking the console down with it.
/// </summary>
public sealed class ConsoleLogWriter : IAsyncDisposable
{
    private readonly StreamWriter _out;
    private readonly TerminalTextFilter _filter = new();
    private readonly Action _closed;
    private readonly ILogger _log;
    private readonly Lock _lock = new();
    private bool _stopped;

    internal ConsoleLogWriter(string filePath, Stream stream, Action closed, ILogger log)
    {
        FilePath = filePath;
        _out = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        _closed = closed;
        _log = log;
    }

    public string FilePath { get; }

    /// <summary>Bytes from the device, exactly as the terminal was shown them.</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            if (_stopped) return;
            var text = _filter.Feed(data);
            if (text.Length > 0) Append(text);
        }
    }

    /// <summary>A line of syscmd's own, set off so it cannot be mistaken for the device's output.</summary>
    public void Note(string message)
    {
        lock (_lock)
        {
            if (_stopped) return;
            Append($"{_filter.EndLine()}*** {message} ***\n");
        }
    }

    private void Append(string text)
    {
        try
        {
            _out.Write(text);
        }
        catch (Exception ex)
        {
            _stopped = true;
            _log.LogError(ex, "Console log {Path} stopped: it could not be written", FilePath);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            _stopped = true;
            try { _out.Dispose(); }
            catch (Exception ex) { _log.LogWarning(ex, "Console log {Path} did not close cleanly", FilePath); }
        }

        _closed();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Turns what a terminal is sent into text a person can read in a file. Escape sequences - colour,
/// cursor movement, window titles - are dropped rather than rendered, line endings become plain
/// newlines, and a backspace takes back the character before it where that is still to hand. It
/// keeps its state between calls, because the device's output arrives in whatever chunks TCP
/// happens to deliver and a sequence is often split across two of them.
/// </summary>
internal sealed class TerminalTextFilter
{
    private enum State { Text, Escape, Csi, String, StringEscape, Charset }

    private State _state;
    private bool _pendingCr;
    private bool _atLineStart = true;

    public string Feed(ReadOnlySpan<byte> data)
    {
        var text = new StringBuilder(data.Length);

        foreach (var b in data)
        {
            // Latin-1: every byte is a character, so nothing a vintage machine sends is lost to a
            // decoder deciding it is not valid UTF-8.
            var c = (char)b;

            switch (_state)
            {
                case State.Text:
                    Text(text, c);
                    break;

                case State.Escape:
                    _state = c switch
                    {
                        '[' => State.Csi,
                        ']' or 'P' or 'X' or '^' or '_' => State.String,
                        '(' or ')' or '*' or '+' or '-' or '.' or '/' or '#' or '%' => State.Charset,
                        _ => State.Text,    // a two-character sequence, now complete
                    };
                    break;

                case State.Csi:
                    // Parameters and intermediates run until a final byte. A control character in
                    // the middle means the sequence was garbage; give the text back rather than
                    // swallowing the rest of the line.
                    if (c is >= '@' and <= '~') _state = State.Text;
                    else if (c < ' ') { _state = State.Text; Text(text, c); }
                    break;

                case State.String:
                    if (c == '\a') _state = State.Text;
                    else if (c == '\x1b') _state = State.StringEscape;
                    break;

                case State.StringEscape:
                    _state = c == '\\' ? State.Text : State.String;
                    break;

                case State.Charset:
                    _state = State.Text;
                    break;
            }
        }

        if (text.Length > 0) _atLineStart = text[^1] == '\n';
        return text.ToString();
    }

    /// <summary>A newline when the text so far stops part way along a line, so a note gets its own.</summary>
    public string EndLine()
    {
        var needed = _pendingCr || !_atLineStart;
        _pendingCr = false;
        _atLineStart = true;
        return needed ? "\n" : "";
    }

    private void Text(StringBuilder text, char c)
    {
        if (_pendingCr)
        {
            // CR LF, telnet's CR NUL and a lone CR all end the line. A lone one really means
            // "back to the start of this line", but a file cannot overwrite itself, and keeping
            // both versions of the line beats keeping neither.
            _pendingCr = false;
            text.Append('\n');
            if (c is '\n' or '\0') return;
        }

        switch (c)
        {
            case '\x1b':
                _state = State.Escape;
                return;
            case '\r':
                _pendingCr = true;
                return;
            case '\n' or '\t':
                text.Append(c);
                return;
            case '\b':
                if (text.Length > 0 && text[^1] != '\n') text.Length--;
                return;
        }

        // Every other control - bell, shift in and out, the C1 set - has no meaning in a file.
        if (c < ' ' || c is >= '\x7f' and < '\xa0') return;
        text.Append(c);
    }
}
