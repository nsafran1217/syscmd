using System.Text;

namespace SysCmd.Core.Theming;

/// <summary>What the browser was asked to paint: a palette, a backdrop, and the backdrop's tint.</summary>
public sealed record ThemeChoice(string Palette, string Backdrop, int BackdropColorSet)
{
    /// <summary>The query string the stylesheet and backdrop endpoints are addressed by.</summary>
    public string Query => $"p={Uri.EscapeDataString(Palette)}&b={Uri.EscapeDataString(Backdrop)}&cs={BackdropColorSet}";
}

/// <summary>
/// Turns a palette into the custom properties the stylesheet reads.
///
/// Only the raw colour sets go in here, named for their CDE ids. Which set paints which surface is
/// decided in cde.css, next to the rules that use them, so the mapping stays visible where anyone
/// working on the look would look for it - see the aliases at the top of that file.
/// </summary>
public static class ThemeCss
{
    public static string Render(CdePalette palette, ThemeChoice choice, RenderedBackdrop? backdrop)
    {
        var css = new StringBuilder();

        css.Append("/* ").Append(palette.Name).Append(" - generated from a CDE .dp palette by Motif's own\n")
           .Append("   colour calculation. Every value below is derived; nothing here is hand-picked. */\n\n");

        css.Append(":root {\n");

        // Native form controls have to know whether they are sitting on a light or a dark desktop,
        // and Motif already decided that when it picked black or white text for the widget face.
        css.Append("    color-scheme: ").Append(palette.IsLight ? "light" : "dark").Append(";\n\n");

        for (var id = 1; id <= 8; id++)
        {
            var set = palette.Set(id);
            css.Append("    --cs").Append(id).Append("-bg: ").Append(set.Bg.Hex).Append(';');
            css.Append(" --cs").Append(id).Append("-fg: ").Append(set.Fg.Hex).Append(';');
            css.Append(" --cs").Append(id).Append("-ts: ").Append(set.TopShadow.Hex).Append(';');
            css.Append(" --cs").Append(id).Append("-bs: ").Append(set.BottomShadow.Hex).Append(';');
            css.Append(" --cs").Append(id).Append("-sel: ").Append(set.Select.Hex).Append(";\n");
        }

        // dtwm paints the root window in the backdrop colour set's bottom shadow and stencils the
        // pattern over it in that set's background (_WmBackdropBgDefault / _WmBackdropFgDefault).
        // Naming the ground colour separately means the desktop is already the right colour before
        // the tile has loaded, and stays right if it never does.
        var ground = palette.Set(choice.BackdropColorSet).BottomShadow;
        css.Append("\n    --desktop-ground: ").Append(ground.Hex).Append(";\n");

        if (backdrop is not null && !string.Equals(choice.Backdrop, BackdropStore.NoBackdrop, StringComparison.OrdinalIgnoreCase))
        {
            css.Append("    --desktop-image: url(\"/cde/backdrop.png?").Append(choice.Query).Append("\");\n");
            css.Append("    --desktop-size: ").Append(backdrop.Width).Append("px ").Append(backdrop.Height).Append("px;\n");
        }
        else
        {
            css.Append("    --desktop-image: none;\n");
            css.Append("    --desktop-size: auto;\n");
        }

        css.Append("}\n");
        return css.ToString();
    }
}
