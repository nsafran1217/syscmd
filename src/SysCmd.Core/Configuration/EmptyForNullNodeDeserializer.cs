using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace SysCmd.Core.Configuration;

/// <summary>
/// Turns a key written with nothing under it into an empty collection rather than a null one.
///
/// YAML says <c>poweroff:</c> with no steps beneath it is a null value, and YamlDotNet duly hands
/// back null - so a half-written config file used to plant a null where every reader expects a
/// list, and the first thing to touch it threw from somewhere far away. An empty collection means
/// the same thing to everyone downstream ("nothing here") and cannot take the app down; the
/// validator reports the empty key so the operator still hears about it.
/// </summary>
public sealed class EmptyForNullNodeDeserializer : INodeDeserializer
{
    public bool Deserialize(
        IParser parser, Type expectedType, Func<IParser, Type, object?> nestedObjectDeserializer,
        out object? value, ObjectDeserializer rootDeserializer)
    {
        value = null;
        if (!parser.Accept<Scalar>(out var scalar) || !IsNull(scalar)) return false;
        if (!TryCreateEmpty(expectedType, out value)) return false;

        parser.MoveNext();
        return true;
    }

    /// <summary>The plain scalars YAML treats as null. A quoted "" is a real empty string.</summary>
    private static bool IsNull(Scalar scalar) =>
        scalar.Style == ScalarStyle.Plain &&
        scalar.Value is "" or "~" or "null" or "Null" or "NULL";

    private static bool TryCreateEmpty(Type type, out object? value)
    {
        value = null;

        // A string is a collection of characters as far as the runtime is concerned, and an array
        // has no parameterless constructor; neither is what this is for.
        if (type == typeof(string) || type.IsArray) return false;
        if (!typeof(System.Collections.IEnumerable).IsAssignableFrom(type)) return false;

        if (type.IsInterface || type.IsAbstract)
        {
            if (!type.IsGenericType) return false;
            var args = type.GetGenericArguments();
            type = args.Length switch
            {
                1 => typeof(List<>).MakeGenericType(args),
                2 => typeof(Dictionary<,>).MakeGenericType(args),
                _ => type,
            };
        }

        if (type.IsInterface || type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null) return false;

        value = Activator.CreateInstance(type);
        return true;
    }
}
