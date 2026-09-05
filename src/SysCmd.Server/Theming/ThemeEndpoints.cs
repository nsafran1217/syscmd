using Microsoft.AspNetCore.Http.Extensions;
using SysCmd.Core.Theming;

namespace SysCmd.Server.Theming;

/// <summary>
/// Serves the current theme: a stylesheet of custom properties, and the backdrop tile tinted to
/// match it. Both are generated rather than stored, because a palette times a backdrop times a
/// colour set is far more combinations than are worth keeping on disk.
///
/// These carry no lab data - just colours - so unlike the API they are not behind
/// <c>RequireLabAccess</c>. They are as public as the rest of wwwroot.
/// </summary>
public static class ThemeEndpoints
{
    public static void MapSysCmdTheme(this WebApplication app)
    {
        app.MapGet("/cde/theme.css", (
            HttpContext http, string? p, string? b, int cs,
            ThemeResolver resolver, PaletteStore palettes, BackdropStore backdrops) =>
        {
            var choice = resolver.Sanitise(p, b, cs);
            var palette = palettes.Get(choice.Palette);
            var backdrop = backdrops.Render(choice.Backdrop, palette, choice.BackdropColorSet);

            var css = ThemeCss.Render(palette, choice, backdrop);
            return Cached(http, Results.Text(css, "text/css"), $"\"{choice.Query}-{css.Length:x}\"");
        });

        app.MapGet("/cde/backdrop.png", (
            HttpContext http, string? p, string? b, int cs,
            ThemeResolver resolver, PaletteStore palettes, BackdropStore backdrops) =>
        {
            var choice = resolver.Sanitise(p, b, cs);
            var rendered = backdrops.Render(choice.Backdrop, palettes.Get(choice.Palette), choice.BackdropColorSet);
            if (rendered is null) return Results.NotFound();

            return Cached(http, Results.Bytes(rendered.Png, "image/png"), rendered.ETag);
        });
    }

    /// <summary>
    /// Both responses are a pure function of their query string, so they can be cached hard. The
    /// ETag makes a theme switch cost one 304 for the tile the browser already has.
    /// </summary>
    private static IResult Cached(HttpContext http, IResult result, string etag)
    {
        if (http.Request.Headers.IfNoneMatch.Contains(etag)) return Results.StatusCode(StatusCodes.Status304NotModified);

        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = "public, max-age=3600";
        return result;
    }
}
