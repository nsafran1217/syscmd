using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SysCmd.Core.Configuration;

/// <summary>
/// Reads and writes the YAML config files. Writes are atomic (temp file + rename) so a crash
/// mid-save can never leave a half-written config on disk.
/// </summary>
public static class YamlIo
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    public static T Parse<T>(string yaml) where T : new()
        => Deserializer.Deserialize<T>(yaml) ?? new T();

    public static T Load<T>(string path) where T : new()
        => Parse<T>(File.ReadAllText(path));

    public static string ToYaml<T>(T value) => Serializer.Serialize(value!);

    /// <summary>Serialise and write atomically, creating the containing directory if needed.</summary>
    public static void Save<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        WriteAtomic(path, ToYaml(value));
    }

    /// <summary>Write raw text atomically. Used by the raw-YAML editor in the GUI.</summary>
    public static void WriteAtomic(string path, string contents)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }
}
