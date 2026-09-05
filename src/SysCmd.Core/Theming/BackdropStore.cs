using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SysCmd.Core.Theming;

/// <summary>A backdrop as rendered for one palette and colour set.</summary>
public sealed record RenderedBackdrop(string ETag, byte[] Png, int Width, int Height);

/// <summary>
/// The desktop backdrops, tinted the way CDE tints them.
///
/// The files are stencils rather than pictures, so the same thirty tiles look completely different
/// under each palette. Parsing is done once per file and the encoded PNG is cached per
/// (backdrop, palette, colour set), which is what keeps a theme switch from re-reading anything.
/// </summary>
public sealed class BackdropStore
{
    /// <summary>The name CDE gives a bare, patternless desktop.</summary>
    public const string NoBackdrop = "NoBackdrop";

    private readonly IReadOnlyList<string> _directories;
    private readonly ILogger<BackdropStore> _log;
    private readonly ConcurrentDictionary<string, BackdropImage> _parsed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RenderedBackdrop> _rendered = new(StringComparer.Ordinal);
    private Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

    public BackdropStore(IEnumerable<string> directories, ILogger<BackdropStore> log)
    {
        _directories = directories.ToList();
        _log = log;
        Load();
    }

    /// <summary>Every backdrop name found, ordered.</summary>
    public IReadOnlyList<string> Names { get; private set; } = [];

    public bool Contains(string? name) => name is not null && _files.ContainsKey(name);

    public void Load()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in _directories)
        {
            if (!Directory.Exists(dir)) continue;

            // .pm wins over .bm where a backdrop ships as both, matching the Style Manager's list.
            foreach (var file in Directory.EnumerateFiles(dir, "*.bm").Concat(Directory.EnumerateFiles(dir, "*.pm")))
                files[Path.GetFileNameWithoutExtension(file)] = file;
        }

        _files = files;
        Names = files.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        _parsed.Clear();
        _rendered.Clear();
    }

    /// <summary>
    /// Renders a backdrop for one palette and colour set, or null when it cannot be read - a
    /// backdrop that will not parse should cost the desktop its pattern, not its paint.
    /// </summary>
    public RenderedBackdrop? Render(string name, CdePalette palette, int colorSetId)
    {
        var key = $"{name} {palette.Name} {colorSetId}";
        if (_rendered.TryGetValue(key, out var cached)) return cached;

        var image = Parse(name);
        if (image is null) return null;

        // NoBackdrop means a bare desktop, not the black tile its placeholder file happens to
        // contain. Serving the ground colour keeps the Style Manager's preview honest and makes
        // the endpoint agree with the stylesheet, which drops the image entirely for this one.
        if (string.Equals(name, NoBackdrop, StringComparison.OrdinalIgnoreCase))
            image = new BackdropImage
            {
                Name = name, Width = 1, Height = 1, Pixels = [0],
                Colors = [new BackdropColor(MotifSymbol.StencilOff, default)],
            };

        var colors = image.Resolve(palette.Set(colorSetId));
        var png = IndexedPng.Encode(image.Width, image.Height, image.Pixels, colors);

        var rendered = new RenderedBackdrop(
            ETag: $"\"{name}-{palette.Name}-{colorSetId}-{png.Length:x}\"",
            Png: png,
            Width: image.Width,
            Height: image.Height);

        _rendered[key] = rendered;
        return rendered;
    }

    private BackdropImage? Parse(string name)
    {
        if (_parsed.TryGetValue(name, out var cached)) return cached;
        if (!_files.TryGetValue(name, out var path)) return null;

        try
        {
            var image = BackdropReader.Read(path);
            _parsed[name] = image;
            return image;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Backdrop {File} could not be read: {Message}", path, ex.Message);
            return null;
        }
    }
}
