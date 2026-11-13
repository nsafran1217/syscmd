using System.Text;

namespace SysCmd.Core.Consoles;

/// <summary>
/// The notation a custom Send menu entry is written in, so a control key can be typed into a YAML
/// file or a text box without either of them mangling it.
///
/// <list type="bullet">
///   <item><c>^X</c> is the control character for X, the way a terminal prints it: <c>^T</c> is
///         Ctrl-T, <c>^[</c> is Escape, <c>^\</c> is the break most console servers listen for,
///         <c>^?</c> is DEL. Case does not matter.</item>
///   <item><c>\r</c>, <c>\n</c>, <c>\t</c>, <c>\e</c> (Escape), <c>\b</c>, <c>\0</c> and
///         <c>\xHH</c> are the usual escapes; <c>\^</c> and <c>\\</c> are a literal caret and
///         backslash.</item>
///   <item>Anything else is sent exactly as written, so <c>~.</c> or <c>#.</c> work as they
///         are.</item>
/// </list>
/// </summary>
public static class SendKeyNotation
{
    /// <summary>The bytes to send, or null with <paramref name="error"/> saying what is wrong.</summary>
    public static string? TryParse(string? notation, out string error)
    {
        error = "";
        if (string.IsNullOrEmpty(notation))
        {
            error = "nothing to send";
            return null;
        }

        var text = new StringBuilder(notation.Length);
        for (var i = 0; i < notation.Length; i++)
        {
            var c = notation[i];

            if (c == '^')
            {
                if (i + 1 >= notation.Length)
                {
                    error = "'^' at the end needs a key after it (write \\^ for a literal caret)";
                    return null;
                }

                var key = char.ToUpperInvariant(notation[++i]);
                if (key == '?') text.Append('\u007f');
                else if (key is >= '@' and <= '_') text.Append((char)(key - '@'));
                else
                {
                    error = $"'^{notation[i]}' is not a control key (use ^@ to ^_, a letter, or ^?)";
                    return null;
                }
                continue;
            }

            if (c == '\\')
            {
                if (i + 1 >= notation.Length)
                {
                    error = "'\\' at the end needs a character after it (write \\\\ for a backslash)";
                    return null;
                }

                var escape = notation[++i];
                switch (escape)
                {
                    case 'r': text.Append('\r'); break;
                    case 'n': text.Append('\n'); break;
                    case 't': text.Append('\t'); break;
                    case 'e': text.Append('\u001b'); break;
                    case 'b': text.Append('\b'); break;
                    case '0': text.Append('\0'); break;
                    case '\\': text.Append('\\'); break;
                    case '^': text.Append('^'); break;
                    case 'x':
                        // ASCII only: the browser sends keystrokes as UTF-8, so anything above
                        // 7F would leave as two bytes rather than the one written here.
                        if (i + 2 < notation.Length
                            && Uri.IsHexDigit(notation[i + 1]) && Uri.IsHexDigit(notation[i + 2])
                            && Convert.ToByte(notation.Substring(i + 1, 2), 16) is var value and <= 0x7f)
                        {
                            text.Append((char)value);
                            i += 2;
                            break;
                        }
                        error = "'\\x' needs two hex digits from 00 to 7F after it, e.g. \\x14";
                        return null;
                    default:
                        error = $"'\\{escape}' is not an escape this understands";
                        return null;
                }
                continue;
            }

            text.Append(c);
        }

        return text.ToString();
    }
}
