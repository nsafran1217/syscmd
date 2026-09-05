using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace SysCmd.Core.Configuration;

/// <summary>
/// Owns the config directory: loads it into an immutable <see cref="ConfigSnapshot"/>, saves
/// individual objects back atomically, and reloads when files change on disk. This is the single
/// writer for everything under the config root.
/// </summary>
public sealed class ConfigStore : IDisposable
{
    private readonly ILogger<ConfigStore> _log;
    private readonly Lock _saveLock = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private volatile ConfigSnapshot _current = ConfigSnapshot.Empty;

    public ConfigStore(ConfigPaths paths, ILogger<ConfigStore> log)
    {
        Paths = paths;
        _log = log;
    }

    public ConfigPaths Paths { get; }

    /// <summary>The most recent successful load. Never null; empty before the first Load().</summary>
    public ConfigSnapshot Current => _current;

    /// <summary>Raised after any reload, whether triggered by a save or by an external edit.</summary>
    public event Action<ConfigSnapshot>? Changed;

    // ------------------------------------------------------------------ load

    public ConfigSnapshot Load()
    {
        Paths.EnsureDirectories();
        var issues = new List<ConfigIssue>();

        var app = LoadOne<AppConfig>(Paths.AppFile, "app.yaml", issues) ?? new AppConfig();

        var pduTypes = LoadDir<PduTypeDefinition>(Paths.PduTypesDir, "pdu-types", issues, (o, id) => o.Id = id);
        var mpTypes = LoadDir<MpTypeDefinition>(Paths.MpTypesDir, "mp-types", issues, (o, id) => o.Id = id);
        var pdus = LoadDir<PduConfig>(Paths.PdusDir, "pdus", issues, (o, id) => { if (string.IsNullOrEmpty(o.Id)) o.Id = id; });
        var servers = LoadDir<ConsoleServerConfig>(Paths.ConsoleServersDir, "console-servers", issues, (o, id) => { if (string.IsNullOrEmpty(o.Id)) o.Id = id; });
        var machines = LoadDir<MachineConfig>(Paths.MachinesDir, "machines", issues, (o, id) => { if (string.IsNullOrEmpty(o.Id)) o.Id = id; });

        var groups = File.Exists(Paths.GroupsFile)
            ? (LoadOne<GroupsFile>(Paths.GroupsFile, "groups.yaml", issues)?.Groups ?? [])
            : [];

        issues.AddRange(ConfigValidator.Validate(app, pduTypes, mpTypes, pdus, servers, machines, groups));

        var snapshot = new ConfigSnapshot
        {
            App = app,
            PduTypes = pduTypes.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            MpTypes = mpTypes.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            Pdus = pdus.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            ConsoleServers = servers.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            Machines = machines.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            Groups = [.. groups],
            Issues = [.. issues],
        };

        _current = snapshot;
        foreach (var issue in issues.Where(i => i.Severity == ConfigIssueSeverity.Error))
            _log.LogWarning("Config error in {File}: {Message}", issue.File, issue.Message);

        _log.LogInformation(
            "Loaded config from {Root}: {Machines} machines, {Pdus} PDUs, {Groups} groups, {Errors} errors",
            Paths.Root, machines.Count, pdus.Count, groups.Count,
            issues.Count(i => i.Severity == ConfigIssueSeverity.Error));

        Changed?.Invoke(snapshot);
        return snapshot;
    }

    private T? LoadOne<T>(string path, string label, List<ConfigIssue> issues) where T : new()
    {
        if (!File.Exists(path)) return default;
        try { return YamlIo.Load<T>(path); }
        catch (Exception ex)
        {
            issues.Add(new(ConfigIssueSeverity.Error, label, $"could not be parsed: {ex.Message}"));
            return default;
        }
    }

    /// <summary>
    /// Load every yaml file in a directory, keyed by file stem. A file that fails to parse becomes
    /// an issue and is skipped, so one bad edit does not take the whole directory down.
    /// </summary>
    private Dictionary<string, T> LoadDir<T>(
        string dir, string label, List<ConfigIssue> issues, Action<T, string> assignId) where T : new()
    {
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in ConfigPaths.YamlFilesIn(dir))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            try
            {
                var obj = YamlIo.Load<T>(file);
                assignId(obj, id);
                result[id] = obj;
            }
            catch (Exception ex)
            {
                issues.Add(new(ConfigIssueSeverity.Error, $"{label}/{id}.yaml", $"could not be parsed: {ex.Message}"));
            }
        }
        return result;
    }

    // ------------------------------------------------------------------ save

    public void SaveApp(AppConfig app) => SaveAndReload(Paths.AppFile, app);
    public void SaveMachine(MachineConfig machine) => SaveAndReload(Paths.MachineFile(Require(machine.Id)), machine);
    public void SavePdu(PduConfig pdu) => SaveAndReload(Paths.PduFile(Require(pdu.Id)), pdu);
    public void SaveConsoleServer(ConsoleServerConfig cs) => SaveAndReload(Paths.ConsoleServerFile(Require(cs.Id)), cs);
    public void SaveGroups(IEnumerable<GroupConfig> groups)
        => SaveAndReload(Paths.GroupsFile, new GroupsFile { Groups = [.. groups] });

    public void DeleteMachine(string id) => DeleteAndReload(Paths.MachineFile(Require(id)));
    public void DeletePdu(string id) => DeleteAndReload(Paths.PduFile(Require(id)));
    public void DeleteConsoleServer(string id) => DeleteAndReload(Paths.ConsoleServerFile(Require(id)));

    /// <summary>Raw file contents, for the GUI's YAML escape hatch.</summary>
    public string ReadRaw(string relativePath)
        => File.ReadAllText(SafePath(relativePath));

    /// <summary>Write raw YAML after checking it parses as the expected shape.</summary>
    public void WriteRaw(string relativePath, string yaml)
    {
        var full = SafePath(relativePath);
        lock (_saveLock)
        {
            YamlIo.WriteAtomic(full, yaml);
        }
        Load();
    }

    private void SaveAndReload<T>(string path, T value)
    {
        lock (_saveLock) { YamlIo.Save(path, value); }
        Load();
    }

    private void DeleteAndReload(string path)
    {
        lock (_saveLock) { if (File.Exists(path)) File.Delete(path); }
        Load();
    }

    private static string Require(string id)
        => string.IsNullOrWhiteSpace(id)
            ? throw new ArgumentException("An id is required to write a config file.")
            : id.Any(c => c is '/' or '\\' or ':' || Path.GetInvalidFileNameChars().Contains(c))
                ? throw new ArgumentException($"'{id}' is not a valid config id.")
                : id;

    /// <summary>Resolve a caller-supplied relative path, refusing anything outside the config root.</summary>
    private string SafePath(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(Paths.Root, relativePath));
        if (!full.StartsWith(Paths.Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException($"'{relativePath}' resolves outside the config directory.");
        return full;
    }

    // --------------------------------------------------------------- watching

    /// <summary>
    /// Reload when files change underneath us, so hand edits show up without a restart. Debounced,
    /// because editors and our own atomic saves fire several events per logical change.
    /// </summary>
    public void StartWatching()
    {
        if (_watcher is not null) return;
        Paths.EnsureDirectories();

        _debounce = new Timer(_ =>
        {
            try { Load(); }
            catch (Exception ex) { _log.LogError(ex, "Reload after a file change failed"); }
        }, null, Timeout.Infinite, Timeout.Infinite);

        _watcher = new FileSystemWatcher(Paths.Root, "*.yaml")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };

        void Bump(object _, FileSystemEventArgs __) => _debounce?.Change(500, Timeout.Infinite);
        _watcher.Changed += Bump;
        _watcher.Created += Bump;
        _watcher.Deleted += Bump;
        _watcher.Renamed += (s, e) => Bump(s, e);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
