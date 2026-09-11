using System.Collections;
using System.Text;
using redb.Route.Abstractions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace redb.Route.DataFormats.Yaml;

/// <summary>
/// YAML <see cref="IMessageSerializer"/> (YamlDotNet). Marshals any object; unmarshals to a POCO, or to a
/// dynamic tree (<c>Dictionary&lt;string, object?&gt;</c> / <c>List&lt;object?&gt;</c> / scalars) when the target
/// is <c>object</c>. Errors name the format.
/// </summary>
public sealed class YamlDataFormat : IMessageSerializer
{
    /// <summary>Content type the format is registered under.</summary>
    public const string DefaultContentType = "application/yaml";

    private readonly YamlDataFormatOptions _options;
    private readonly ISerializer _serializer;
    private readonly IDeserializer _deserializer;

    /// <summary>Creates the format with the given options (defaults when <c>null</c>).</summary>
    public YamlDataFormat(YamlDataFormatOptions? options = null)
    {
        _options = options ?? new YamlDataFormatOptions();
        _serializer = new SerializerBuilder().WithNamingConvention(_options.NamingConvention).Build();
        var builder = new DeserializerBuilder().WithNamingConvention(_options.NamingConvention);
        if (_options.IgnoreUnmatchedProperties) builder = builder.IgnoreUnmatchedProperties();
        _deserializer = builder.Build();
    }

    /// <summary>The options in effect.</summary>
    public YamlDataFormatOptions Options => _options;

    /// <inheritdoc />
    public string ContentType => DefaultContentType;

    /// <inheritdoc />
    public IReadOnlyCollection<string> MediaTypes => [DefaultContentType, "application/x-yaml", "text/yaml", "text/x-yaml"];

    /// <inheritdoc />
    public byte[] Serialize<T>(T value)
    {
        switch (value)
        {
            case null: return [];
            case byte[] bytes: return bytes;
            case string text: return Encoding.UTF8.GetBytes(text);
        }

        try
        {
            return Encoding.UTF8.GetBytes(_serializer.Serialize(value));
        }
        catch (YamlException ex)
        {
            throw new InvalidOperationException($"YAML data format: cannot marshal {value.GetType().Name} ({ex.Message}).", ex);
        }
    }

    /// <inheritdoc />
    public T? Deserialize<T>(byte[] data) => (T?)Deserialize(data, typeof(T));

    /// <inheritdoc />
    public object? Deserialize(byte[] data, Type type)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(type);

        var text = Encoding.UTF8.GetString(data);
        if (type == typeof(string)) return text;

        try
        {
            return type == typeof(object)
                ? Normalize(_deserializer.Deserialize(text))
                : _deserializer.Deserialize(text, type);
        }
        catch (YamlException ex)
        {
            throw new InvalidOperationException($"YAML data format: the body is not valid YAML for {type.Name} ({ex.Message}).", ex);
        }
    }

    /// <summary>YamlDotNet's dynamic tree uses <c>Dictionary&lt;object, object&gt;</c>; expressions and templates want string keys.</summary>
    private static object? Normalize(object? node) => node switch
    {
        IDictionary dictionary => NormalizeMap(dictionary),
        string s => s,
        IEnumerable list => list.Cast<object?>().Select(Normalize).ToList(),
        _ => node,
    };

    private static Dictionary<string, object?> NormalizeMap(IDictionary dictionary)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in dictionary)
            result[entry.Key?.ToString() ?? string.Empty] = Normalize(entry.Value);
        return result;
    }
}
