namespace SysCmd.Core.Configuration;

/// <summary>Resolves the on-disk layout of the config directory.</summary>
public sealed class ConfigPaths(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    public string AppFile => Path.Combine(Root, "app.yaml");
    public string GroupsFile => Path.Combine(Root, "groups.yaml");
    public string PduTypesDir => Path.Combine(Root, "pdu-types");
    public string PdusDir => Path.Combine(Root, "pdus");
    public string MpTypesDir => Path.Combine(Root, "mp-types");
    public string ConsoleServersDir => Path.Combine(Root, "console-servers");
    public string MachinesDir => Path.Combine(Root, "machines");

    public string PduFile(string id) => Path.Combine(PdusDir, id + ".yaml");
    public string MachineFile(string id) => Path.Combine(MachinesDir, id + ".yaml");
    public string ConsoleServerFile(string id) => Path.Combine(ConsoleServersDir, id + ".yaml");
    public string PduTypeFile(string id) => Path.Combine(PduTypesDir, id + ".yaml");
    public string MpTypeFile(string id) => Path.Combine(MpTypesDir, id + ".yaml");

    public void EnsureDirectories()
    {
        foreach (var d in new[] { Root, PduTypesDir, PdusDir, MpTypesDir, ConsoleServersDir, MachinesDir })
            Directory.CreateDirectory(d);
    }

    /// <summary>Yaml files in a directory, sorted, ignoring temp files from an interrupted save.</summary>
    public static IEnumerable<string> YamlFilesIn(string dir)
        => Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.yaml").Where(f => !f.EndsWith(".tmp")).OrderBy(f => f)
            : [];
}
