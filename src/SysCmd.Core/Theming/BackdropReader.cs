using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SysCmd.Core.Theming;

/// <summary>
/// Reads the two image formats CDE ships its backdrops in: X bitmaps (.bm), which are one-bit
/// stencils, and XPM pixmaps (.pm), whose colour tables name Motif resources so the desktop can
/// re-tint them per palette.
/// </summary>
public static partial class BackdropReader
{
    public static BackdropImage Read(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var text = File.ReadAllText(path);

        return Path.GetExtension(path).Equals(".bm", StringComparison.OrdinalIgnoreCase)
            ? ReadXbm(name, text)
            : ReadXpm(name, text);
    }

    // ------------------------------------------------------------------------------ X bitmap

    /// <summary>
    /// An XBM is "#define name_width N", "#define name_height N" and a byte array. Bits are packed
    /// least-significant-first and every row starts on a byte boundary, so a 261-pixel row is 33
    /// bytes with seven bits of padding.
    /// </summary>
    private static BackdropImage ReadXbm(string name, string text)
    {
        var width = DefineValue(text, "width");
        var height = DefineValue(text, "height");

        var open = text.IndexOf('{');
        var close = text.LastIndexOf('}');
        if (open < 0 || close < open) throw new FormatException("no bitmap data");

        var bytes = new List<byte>((width + 7) / 8 * height);
        foreach (Match m in ByteLiteral().Matches(text[(open + 1)..close]))
            bytes.Add((byte)int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));

        var stride = (width + 7) / 8;
        if (bytes.Count < stride * height) throw new FormatException("bitmap data is short");

        var pixels = new byte[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                pixels[y * width + x] = (byte)((bytes[y * stride + (x >> 3)] >> (x & 7)) & 1);

        return new BackdropImage
        {
            Name = name,
            Width = width,
            Height = height,
            Pixels = pixels,
            // Index 0 is a clear bit, index 1 a set bit. See MotifSymbol for why they land on the
            // colour set's bottom shadow and background rather than on a foreground/background pair.
            Colors = [new(MotifSymbol.StencilOff, default), new(MotifSymbol.StencilOn, default)],
        };
    }

    private static int DefineValue(string text, string suffix)
    {
        var m = Regex.Match(text, $@"#define\s+\w*_{suffix}\s+(\d+)");
        if (!m.Success) throw new FormatException($"no {suffix} define");
        return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    // -------------------------------------------------------------------------------- XPM

    private static readonly Dictionary<string, MotifSymbol> Symbols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["background"] = MotifSymbol.Background,
        ["foreground"] = MotifSymbol.Foreground,
        ["topshadowcolor"] = MotifSymbol.TopShadow,
        ["bottomshadowcolor"] = MotifSymbol.BottomShadow,
        ["selectcolor"] = MotifSymbol.Select,
    };

    /// <summary>
    /// Reads an XPM3 pixmap. Only the five Motif resource names above track the palette; anything
    /// else - the iconGray1..8 ramp these files also use - keeps the literal colour in the file,
    /// which is exactly what Motif does with symbols it was not given an override for.
    /// </summary>
    private static BackdropImage ReadXpm(string name, string text)
    {
        var strings = StringLiterals(text);

        if (strings.Count == 0) throw new FormatException("no XPM data");

        var header = strings[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (header.Length < 4) throw new FormatException($"bad XPM header '{strings[0]}'");

        var width = int.Parse(header[0], CultureInfo.InvariantCulture);
        var height = int.Parse(header[1], CultureInfo.InvariantCulture);
        var count = int.Parse(header[2], CultureInfo.InvariantCulture);
        var perPixel = int.Parse(header[3], CultureInfo.InvariantCulture);

        if (strings.Count < 1 + count + height) throw new FormatException("XPM data is short");

        var colors = new List<BackdropColor>(count);
        var keys = new Dictionary<string, byte>(StringComparer.Ordinal);

        for (var i = 0; i < count; i++)
        {
            var entry = strings[1 + i];
            if (entry.Length < perPixel) throw new FormatException("XPM colour entry is short");

            keys[entry[..perPixel]] = (byte)i;
            colors.Add(ParseColorEntry(entry[perPixel..]));
        }

        var pixels = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            var row = strings[1 + count + y];
            for (var x = 0; x < width; x++)
            {
                var at = x * perPixel;
                // Trailing pixels are occasionally clipped in the shipped files; treat a short row
                // as transparent rather than refusing the whole backdrop.
                var key = at + perPixel <= row.Length ? row.Substring(at, perPixel) : null;
                pixels[y * width + x] = key is not null && keys.TryGetValue(key, out var index) ? index : (byte)0;
            }
        }

        return new BackdropImage
        {
            Name = name, Width = width, Height = height, Pixels = pixels, Colors = colors,
        };
    }

    /// <summary>
    /// Splits the "s symbolic m mono c colour" pairs of one XPM colour entry. The symbolic name
    /// wins when it is one Motif knows; otherwise the visual colour is used, and a file that gives
    /// no visual colour at all (SkyDark.pm does this) falls back to its monochrome one.
    /// </summary>
    private static BackdropColor ParseColorEntry(string spec)
    {
        var tokens = spec.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        string? key = null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var token in tokens)
        {
            if (token is "s" or "m" or "c" or "g" or "g4")
            {
                key = token;
                values[key] = "";
            }
            else if (key is not null)
            {
                values[key] = values[key].Length == 0 ? token : values[key] + " " + token;
            }
        }

        if (values.TryGetValue("s", out var symbolic) && Symbols.TryGetValue(symbolic, out var symbol))
            return new BackdropColor(symbol, default);

        foreach (var field in (string[])["c", "g", "g4", "m"])
        {
            if (!values.TryGetValue(field, out var value)) continue;

            // "None" is XPM's transparent. A backdrop covers the whole root window, so the only
            // sensible thing behind it is the backdrop's own ground colour.
            if (value.Equals("None", StringComparison.OrdinalIgnoreCase))
                return new BackdropColor(MotifSymbol.StencilOff, default);

            if (XColor.TryParse(value, out var color)) return new BackdropColor(MotifSymbol.None, color);
        }

        return new BackdropColor(MotifSymbol.StencilOff, default);
    }

    /// <summary>
    /// Pulls the quoted strings out of an XPM source file, skipping comments.
    ///
    /// Two details matter. Comments have to be recognised by the same pass that tracks string
    /// state, because an XPM pixel row is an arbitrary run of characters and may legitimately
    /// contain a slash and a star. And backslashes are *not* escapes: libXpm reads a file as bytes
    /// rather than compiling it as C, so SkyDark.pm - which uses a bare backslash as a pixel key,
    /// and whose rows are therefore eight consecutive backslashes - only decodes correctly if a
    /// backslash is treated as an ordinary character.
    /// </summary>
    private static List<string> StringLiterals(string text)
    {
        var found = new List<string>();
        var current = new StringBuilder();
        var inString = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inString)
            {
                if (c == '"') { found.Add(current.ToString()); current.Clear(); inString = false; continue; }
                current.Append(c);
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 1;
                continue;
            }

            if (c == '"') inString = true;
        }

        return found;
    }

    [GeneratedRegex(@"0x([0-9a-fA-F]{1,2})")]
    private static partial Regex ByteLiteral();
}
