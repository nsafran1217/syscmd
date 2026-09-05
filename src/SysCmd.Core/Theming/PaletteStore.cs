using Microsoft.Extensions.Logging;

namespace SysCmd.Core.Theming;

/// <summary>
/// Loads CDE .dp palette files. A .dp file is nothing but a list of colour specifications, one per
/// line, up to eight of them - only the background of each colour set is stored, and everything
/// else is derived (<c>dtsession/SrvFile_io.c</c>, <c>ParsePaletteInfo</c>).
///
/// Palettes are read from the shipped asset directory and then from the lab's own
/// <c>config/palettes/</c>, which wins on a name clash. Dropping a .dp file in is all it takes to
/// add a palette, the way dropping a YAML file in is all it takes to add a device type. A file that
/// will not parse is reported and skipped; it never takes the rest of the set down with it.
/// </summary>
public sealed class PaletteStore
{
    private readonly IReadOnlyList<string> _directories;
    private readonly ILogger<PaletteStore> _log;
    private Dictionary<string, CdePalette> _palettes = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _problems = [];

    public PaletteStore(IEnumerable<string> directories, ILogger<PaletteStore> log)
    {
        _directories = directories.ToList();
        _log = log;
        Load();
    }

    /// <summary>Every palette that loaded, ordered by name.</summary>
    public IReadOnlyList<CdePalette> All { get; private set; } = [];

    /// <summary>Files that would not parse, for the configuration page to surface.</summary>
    public IReadOnlyList<string> Problems => _problems;

    /// <summary>
    /// CDE's own out-of-the-box palette (<c>DEFAULT_COLOR_PALETTE</c> in dtsession/SrvPalette.c).
    /// Its colour sets are the salmon title bar, grey frame and slate text areas this UI already had.
    /// </summary>
    public const string DefaultName = "Default";

    public bool Contains(string? name) => name is not null && _palettes.ContainsKey(name);

    /// <summary>Looks a palette up, falling back to Default and then to whatever did load.</summary>
    public CdePalette Get(string? name)
    {
        if (name is not null && _palettes.TryGetValue(name, out var found)) return found;
        if (_palettes.TryGetValue(DefaultName, out var fallback)) return fallback;
        return All.Count > 0 ? All[0] : Monochrome();
    }

    public void Load()
    {
        var loaded = new Dictionary<string, CdePalette>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();

        foreach (var dir in _directories)
        {
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.dp").OrderBy(f => f))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                try
                {
                    loaded[name] = Parse(name, File.ReadAllLines(file));
                }
                catch (Exception ex)
                {
                    problems.Add($"{Path.GetFileName(file)}: {ex.Message}");
                    _log.LogWarning("Palette {File} could not be loaded: {Message}", file, ex.Message);
                }
            }
        }

        _palettes = loaded;
        _problems = problems;
        All = loaded.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Reads the backgrounds out of a .dp file and expands each into a colour set. Parsing stops at
    /// a '!' line, as ParsePaletteInfo does.
    /// </summary>
    public static CdePalette Parse(string name, IEnumerable<string> lines)
    {
        var backgrounds = new List<Rgb>();

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line[0] == '!') break;

            if (!XColor.TryParse(line, out var color))
                throw new FormatException($"line {backgrounds.Count + 1} is not an X colour: '{line}'");

            backgrounds.Add(color);
            if (backgrounds.Count == 8) break;
        }

        if (backgrounds.Count == 0) throw new FormatException("no colours in file");

        return new CdePalette { Name = name, Colors = Expand(backgrounds) };
    }

    /// <summary>
    /// Fills eight colour sets from however many the file gave. CDE does the same thing in reverse
    /// for shallow displays: <c>convert_pixel_set</c> in dtsession/SrvPalette.c maps eight ids down
    /// onto the four or two a palette actually carries. These are those mappings.
    /// </summary>
    private static IReadOnlyList<ColorSet> Expand(List<Rgb> backgrounds)
    {
        int[] mapping = backgrounds.Count switch
        {
            >= 8 => [0, 1, 2, 3, 4, 5, 6, 7],
            >= 4 => [0, 1, 2, 3, 1, 1, 2, 1],
            _ => [0, 1, 1, 1, 1, 1, 1, 1],
        };

        var sets = new ColorSet[8];
        for (var i = 0; i < 8; i++)
            sets[i] = MotifColors.Calculate(backgrounds[Math.Min(mapping[i], backgrounds.Count - 1)]);

        return sets;
    }

    /// <summary>A last-resort palette, so a missing asset directory cannot leave the UI unpainted.</summary>
    private static CdePalette Monochrome() =>
        new() { Name = "Grey", Colors = Expand([Rgb.FromBytes(0x99, 0x99, 0x99)]) };
}
