using System.Globalization;

namespace SysCmd.Core.Theming;

/// <summary>
/// Which colour set paints what. Taken from the code rather than from dtstyle's manual page: that
/// page still documents primary 3 / secondary 4, but Motif's own resource defaults
/// (<c>docs/motif-code/lib/Xm/ColorObj.c</c>) are primary 5, secondary 6, text 4, active 1,
/// inactive 2. <c>docs/CDE/cde/doc/.../session.sgm</c> lists all eight roles in the same order.
/// </summary>
public static class CdeColorSet
{
    /// <summary>Active window borders - the famous salmon title bar in the Default palette.</summary>
    public const int Active = 1;

    /// <summary>Inactive window borders, and the default widget face everything else bevels from.</summary>
    public const int Inactive = 2;

    /// <summary>Text and list areas: sunken fields, the event log, terminal interiors.</summary>
    public const int Text = 4;

    /// <summary>Main window background.</summary>
    public const int Primary = 5;

    /// <summary>Dialog boxes and menu bars.</summary>
    public const int Secondary = 6;

    /// <summary>Front panel background.</summary>
    public const int FrontPanel = 8;

    /// <summary>
    /// The colour sets dtwm cycles through for workspace backdrops, from
    /// <c>DefaultWsColorSetId</c> in <c>dtwm/WmResource.c</c>. Workspace 1 gets 3, 2 gets 5, and
    /// so on. session.sgm claims a different pairing; the code wins.
    /// </summary>
    public static readonly int[] WorkspaceBackdrops = [3, 5, 6, 7];

    public static bool IsBackdropSet(int id) => Array.IndexOf(WorkspaceBackdrops, id) >= 0;
}

/// <summary>
/// One CDE palette: eight colour sets, each expanded by <see cref="MotifColors"/> from the single
/// background the .dp file stores.
/// </summary>
public sealed class CdePalette
{
    public required string Name { get; init; }

    /// <summary>Indexed 0..7. Use <see cref="Set"/> for the 1-based ids CDE and its docs use.</summary>
    public required IReadOnlyList<ColorSet> Colors { get; init; }

    /// <summary>The colour set with the 1-based id CDE uses in its resources and documentation.</summary>
    public ColorSet Set(int id) => Colors[Math.Clamp(id, 1, Colors.Count) - 1];

    /// <summary>
    /// True when the default widget face is light enough that Motif picks black text, which is
    /// what <c>color-scheme</c> needs to know so native form controls match.
    /// </summary>
    public bool IsLight => Set(CdeColorSet.Inactive).Fg is { R: 0, G: 0, B: 0 };
}

public static class XColor
{
    // CDE ships a handful of palettes written with colour names rather than hex - Black.dp,
    // White.dp and the two monochrome ones. Nothing else in the set needs a name table.
    private static readonly Dictionary<string, Rgb> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = new Rgb(0, 0, 0),
        ["white"] = new Rgb(Rgb.Max, Rgb.Max, Rgb.Max),
    };

    /// <summary>
    /// Parses an X colour specification the way XParseColor does for the forms CDE palettes use:
    /// #rgb, #rrggbb and #rrrrggggbbbb, plus the two colour names above. Each form spreads its
    /// digits across the full 16-bit range.
    /// </summary>
    public static bool TryParse(string? text, out Rgb color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = text.Trim();
        if (Names.TryGetValue(s, out color)) return true;
        if (s[0] != '#') return false;

        s = s[1..];
        if (s.Length % 3 != 0) return false;

        var digits = s.Length / 3;
        if (digits is < 1 or > 4) return false;

        if (!TryChannel(s.AsSpan(0, digits), out var r) ||
            !TryChannel(s.AsSpan(digits, digits), out var g) ||
            !TryChannel(s.AsSpan(digits * 2, digits), out var b)) return false;

        color = new Rgb(r, g, b);
        return true;
    }

    private static bool TryChannel(ReadOnlySpan<char> digits, out int value)
    {
        value = 0;
        if (!int.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw)) return false;

        // Scale the shortest forms up so #fff and #ffffffffffff both mean full intensity.
        value = digits.Length switch
        {
            1 => raw * 0x1111,
            2 => raw * 0x0101,
            3 => raw << 4 | raw >> 8,
            _ => raw,
        };
        return true;
    }
}
