namespace SysCmd.Core.Theming;

/// <summary>
/// A colour in X11's 16-bit-per-channel space, which is what CDE palette files store and what
/// Motif's shading arithmetic works in. Narrowing to 8 bits takes the high byte, the way X does,
/// so a palette entry written as <c>#ed00a8007000</c> comes back out as <c>#eda870</c>.
/// </summary>
public readonly record struct Rgb(int R, int G, int B)
{
    public const int Max = 65535;

    public static Rgb FromBytes(int r, int g, int b) => new(r * 257, g * 257, b * 257);

    public string Hex => $"#{R >> 8:x2}{G >> 8:x2}{B >> 8:x2}";

    public override string ToString() => Hex;
}

/// <summary>
/// The five colours Motif derives from one background: what CDE calls a colour set. A palette is
/// eight of these, and every surface in the desktop is painted from one of them.
/// </summary>
public readonly record struct ColorSet(Rgb Bg, Rgb Fg, Rgb TopShadow, Rgb BottomShadow, Rgb Select);

/// <summary>
/// Motif's default colour calculation, ported from <c>docs/motif-code/lib/Xm/Color.c</c>
/// (<c>Brightness</c>, <c>CalculateColorsFor{Dark,Light,Medium}Background</c>, <c>CalculateColorsRGB</c>)
/// with the constants from <c>lib/Xm/ColorP.h</c> and the thresholds from <c>lib/Xm/Xm.h.in</c>.
///
/// CDE stores only a background per colour set and calls this to get the rest
/// (<c>dtsession/SrvFile_io.c</c>, <c>ParsePaletteInfo</c>), so reproducing it exactly is what makes
/// a real palette file look like the real desktop. The integer truncation is deliberate and
/// load-bearing: do not "improve" the arithmetic into floating point.
///
/// NsCDE ships a Python translation of the same routine that reads the red channel when computing
/// the bottom shadow's green. That bug is not reproduced here.
/// </summary>
public static class MotifColors
{
    // ColorP.h: contributions of each primary to overall luminosity, summing to 1.0.
    private const double RedLuminosity = 0.30;
    private const double GreenLuminosity = 0.59;
    private const double BlueLuminosity = 0.11;

    // ColorP.h: percent effect of intensity, light and luminosity on brightness, summing to 100.
    private const int IntensityFactor = 75;
    private const int LightFactor = 0;
    private const int LuminosityFactor = 25;

    // ColorP.h: LITE model - percent to interpolate towards black. Note that on a very light
    // background even the *top* shadow darkens, which is why near-white palettes read as flat.
    private const int LiteSelFactor = 15;
    private const int LiteBsFactor = 40;
    private const int LiteTsFactor = 20;

    // ColorP.h: DARK model - percent to interpolate towards white.
    private const int DarkSelFactor = 15;
    private const int DarkBsFactor = 30;
    private const int DarkTsFactor = 50;

    // ColorP.h: STD model - interpolated between the LO and HI values by brightness.
    private const int HiSelFactor = 15;
    private const int HiBsFactor = 40;
    private const int HiTsFactor = 60;
    private const int LoSelFactor = 15;
    private const int LoBsFactor = 60;
    private const int LoTsFactor = 50;

    // Xm.h.in: percentages, scaled by ColorP.h's XmCOLOR_PERCENTILE (65535/100 == 655).
    private const int Percentile = Rgb.Max / 100;
    private const int DarkThreshold = 20 * Percentile;
    private const int LightThreshold = 93 * Percentile;
    private const int ForegroundThreshold = 70 * Percentile;

    /// <summary>Motif's perceived brightness, 0..65535.</summary>
    public static int Brightness(Rgb c)
    {
        var intensity = (c.R + c.G + c.B) / 3;
        var luminosity = (int)((RedLuminosity * c.R) + (GreenLuminosity * c.G) + (BlueLuminosity * c.B));
        var max = Math.Max(c.R, Math.Max(c.G, c.B));
        var min = Math.Min(c.R, Math.Min(c.G, c.B));
        var light = (min + max) / 2;

        return ((intensity * IntensityFactor) + (light * LightFactor) + (luminosity * LuminosityFactor)) / 100;
    }

    /// <summary>Expands one background into the full colour set, as XmGetColors does.</summary>
    public static ColorSet Calculate(Rgb bg)
    {
        var brightness = Brightness(bg);

        // Motif picks black or white text off a single threshold - no blending, no midpoints.
        var fg = brightness > ForegroundThreshold
            ? new Rgb(0, 0, 0)
            : new Rgb(Rgb.Max, Rgb.Max, Rgb.Max);

        if (brightness < DarkThreshold)
        {
            // Everything lightens towards white.
            return new ColorSet(bg, fg,
                TopShadow: Up(bg, DarkTsFactor),
                BottomShadow: Up(bg, DarkBsFactor),
                Select: Up(bg, DarkSelFactor));
        }

        if (brightness > LightThreshold)
        {
            // Everything darkens towards black, the top shadow included.
            return new ColorSet(bg, fg,
                TopShadow: Down(bg, LiteTsFactor),
                BottomShadow: Down(bg, LiteBsFactor),
                Select: Down(bg, LiteSelFactor));
        }

        // The medium model, which is what every shipped CDE palette actually lands in. The factors
        // ramp with brightness: the bottom shadow eases off (60% -> 40%) and the top shadow
        // strengthens (50% -> 60%) as the background gets lighter.
        var selF = LoSelFactor + (brightness * (HiSelFactor - LoSelFactor) / Rgb.Max);
        var bsF = LoBsFactor + (brightness * (HiBsFactor - LoBsFactor) / Rgb.Max);
        var tsF = LoTsFactor + (brightness * (HiTsFactor - LoTsFactor) / Rgb.Max);

        return new ColorSet(bg, fg,
            TopShadow: Up(bg, tsF),
            BottomShadow: Down(bg, bsF),
            Select: Down(bg, selF));
    }

    /// <summary>Interpolate each channel towards black by <paramref name="percent"/>.</summary>
    private static Rgb Down(Rgb c, int percent) => new(
        c.R - (c.R * percent / 100),
        c.G - (c.G * percent / 100),
        c.B - (c.B * percent / 100));

    /// <summary>Interpolate each channel towards white by <paramref name="percent"/>.</summary>
    private static Rgb Up(Rgb c, int percent) => new(
        c.R + (percent * (Rgb.Max - c.R) / 100),
        c.G + (percent * (Rgb.Max - c.G) / 100),
        c.B + (percent * (Rgb.Max - c.B) / 100));
}
