using SysCmd.Core.Configuration;
using SysCmd.Core.Theming;

namespace SysCmd.Server.Theming;

/// <summary>
/// Works out which palette and backdrop to paint for this request.
///
/// The choice lives in cookies rather than in local storage on purpose. App.razor is rendered
/// server-side, so a cookie is readable before the first byte goes out and the very first paint is
/// already in the right colours - the same reasoning that makes the navigation panel render as
/// nav-auto rather than waiting for a stored preference. Local storage would give a visible flash
/// of the wrong theme on every page load.
/// </summary>
public sealed class ThemeResolver(ConfigStore config, PaletteStore palettes, BackdropStore backdrops)
{
    /// <summary>Palette name, or <see cref="RandomPalette"/>. Absent means "follow the lab default".</summary>
    public const string PaletteCookie = "syscmd.theme";

    /// <summary>Comma-separated palette names to draw from when the palette is random.</summary>
    public const string PoolCookie = "syscmd.themePool";

    public const string BackdropCookie = "syscmd.backdrop";
    public const string BackdropColorSetCookie = "syscmd.backdropCs";

    /// <summary>The sentinel that asks for a different palette on every page load.</summary>
    public const string RandomPalette = "random";

    /// <summary>
    /// Resolves the request's theme. A random choice is re-rolled here, which means it changes on
    /// each full page load and holds steady while navigating inside the app - the document is only
    /// rendered once per load, and Blazor routes the rest client-side.
    /// </summary>
    public ThemeChoice Resolve(HttpContext? http)
    {
        var site = config.Current.App.Site;

        var requested = Cookie(http, PaletteCookie) ?? site.Theme;
        var palette = string.Equals(requested, RandomPalette, StringComparison.OrdinalIgnoreCase)
            ? PickRandom(Pool(http, site))
            : requested;

        if (!palettes.Contains(palette)) palette = palettes.Get(palette).Name;

        var backdrop = Cookie(http, BackdropCookie) ?? site.Backdrop;
        if (!backdrops.Contains(backdrop)) backdrop = backdrops.Contains(BackdropStore.NoBackdrop)
            ? BackdropStore.NoBackdrop
            : backdrops.Names.FirstOrDefault() ?? BackdropStore.NoBackdrop;

        var colorSet = int.TryParse(Cookie(http, BackdropColorSetCookie), out var cs)
            ? cs
            : site.BackdropColorSet;

        return new ThemeChoice(palette, backdrop, Sanitise(colorSet));
    }

    /// <summary>Normalises an arbitrary query or cookie value into a real, renderable choice.</summary>
    public ThemeChoice Sanitise(string? palette, string? backdrop, int colorSet)
    {
        var name = palettes.Get(palette).Name;
        var tile = backdrops.Contains(backdrop) ? backdrop! : BackdropStore.NoBackdrop;
        return new ThemeChoice(name, tile, Sanitise(colorSet));
    }

    /// <summary>
    /// dtwm only ever tints a backdrop with colour sets 3, 5, 6 or 7; anything else is not a
    /// backdrop colour and would give a tile that clashes with the windows on top of it.
    /// </summary>
    private static int Sanitise(int colorSet) =>
        CdeColorSet.IsBackdropSet(colorSet) ? colorSet : CdeColorSet.WorkspaceBackdrops[0];

    /// <summary>The palettes a random choice may draw from: the browser's list, else the lab's, else all.</summary>
    public IReadOnlyList<string> Pool(HttpContext? http, SiteConfig site)
    {
        var fromCookie = (Cookie(http, PoolCookie) ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(palettes.Contains)
            .ToList();

        if (fromCookie.Count > 0) return fromCookie;

        var fromConfig = site.RandomThemes.Where(palettes.Contains).ToList();
        if (fromConfig.Count > 0) return fromConfig;

        return palettes.All.Select(p => p.Name).ToList();
    }

    private static string PickRandom(IReadOnlyList<string> pool) =>
        pool.Count == 0 ? PaletteStore.DefaultName : pool[Random.Shared.Next(pool.Count)];

    /// <summary>
    /// Reads one cookie, undoing the escaping the browser side puts on. A comma is not legal in a
    /// cookie value, so the palette pool travels URL-encoded; without unescaping here the whole
    /// list arrives as one unrecognised name and every random pick falls back to all palettes.
    /// </summary>
    private static string? Cookie(HttpContext? http, string name)
    {
        if (http?.Request.Cookies.TryGetValue(name, out var value) != true || value!.Length == 0) return null;

        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }
}
