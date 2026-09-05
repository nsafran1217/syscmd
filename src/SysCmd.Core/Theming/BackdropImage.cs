namespace SysCmd.Core.Theming;

/// <summary>
/// How one colour in a backdrop resolves against the workspace's colour set. CDE backdrops are not
/// pictures, they are stencils: the XPM colour table names Motif resources rather than colours, and
/// the pixmap loader substitutes the live colour set for those names
/// (<c>docs/motif-code/lib/Xm/ImageCache.c</c>, <c>GetOverrideColors</c>). That is the whole
/// mechanism by which a CDE desktop's wallpaper tracks its palette.
/// </summary>
public enum MotifSymbol
{
    /// <summary>Not a Motif resource - use the literal colour the file gives.</summary>
    None,
    Background,
    Foreground,
    TopShadow,
    BottomShadow,
    Select,

    /// <summary>
    /// A set bit in a two-colour XBM. dtwm passes the colour set's *background* as the pixmap's
    /// foreground (<c>_WmBackdropFgDefault</c> in dtwm/WmResource.c).
    /// </summary>
    StencilOn,

    /// <summary>
    /// A clear bit in a two-colour XBM. dtwm passes the colour set's *bottom shadow* as the
    /// pixmap's background (<c>_WmBackdropBgDefault</c>) - which is why a CDE backdrop reads as a
    /// light pattern on a darker ground rather than the other way round.
    /// </summary>
    StencilOff,
}

public readonly record struct BackdropColor(MotifSymbol Symbol, Rgb Literal)
{
    public Rgb Resolve(ColorSet set) => Symbol switch
    {
        MotifSymbol.Background => set.Bg,
        MotifSymbol.Foreground => set.Fg,
        MotifSymbol.TopShadow => set.TopShadow,
        MotifSymbol.BottomShadow => set.BottomShadow,
        MotifSymbol.Select => set.Select,
        MotifSymbol.StencilOn => set.Bg,
        MotifSymbol.StencilOff => set.BottomShadow,
        _ => Literal,
    };
}

/// <summary>A parsed backdrop tile: palette indices, plus how each index is to be coloured.</summary>
public sealed class BackdropImage
{
    public required string Name { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>One index into <see cref="Colors"/> per pixel, row-major.</summary>
    public required byte[] Pixels { get; init; }

    public required IReadOnlyList<BackdropColor> Colors { get; init; }

    /// <summary>Resolves the colour table against a colour set, ready to become a PNG palette.</summary>
    public Rgb[] Resolve(ColorSet set) => Colors.Select(c => c.Resolve(set)).ToArray();
}
