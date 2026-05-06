using redb.Route.Abstractions;

namespace redb.Route.Serialization;

/// <summary>
/// Default registry mapping content types to <see cref="IMessageSerializer"/> instances.
/// Pre-registers JSON and XML serializers. Thread-safe for reads after initialization.
/// </summary>
public sealed class DataFormatRegistry : IDataFormatRegistry
{
    private readonly Dictionary<string, IMessageSerializer> _serializers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IMessageSerializer> _profiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a registry with JSON and XML serializers pre-registered.</summary>
    public DataFormatRegistry()
    {
        var json = new JsonMessageSerializer();
        _serializers["application/json"] = json;

        var xml = new XmlMessageSerializer();
        _serializers["application/xml"] = xml;
        _serializers["text/xml"] = xml;
    }

    /// <inheritdoc />
    public IMessageSerializer? GetSerializer(string contentType)
    {
        ArgumentNullException.ThrowIfNull(contentType);

        // Exact match
        if (_serializers.TryGetValue(contentType, out var s))
            return s;

        // Strip parameters (e.g. "application/json; charset=utf-8" → "application/json")
        var semi = contentType.IndexOf(';');
        if (semi > 0)
        {
            var baseType = contentType[..semi].Trim();
            if (_serializers.TryGetValue(baseType, out s))
                return s;
        }

        // Structured suffix: "application/vnd.api+json" → try "application/json"
        if (contentType.Contains("+json", StringComparison.OrdinalIgnoreCase))
            return _serializers.GetValueOrDefault("application/json");

        if (contentType.Contains("+xml", StringComparison.OrdinalIgnoreCase))
            return _serializers.GetValueOrDefault("application/xml");

        return null;
    }

    /// <inheritdoc />
    public void Register(string contentType, IMessageSerializer serializer)
    {
        ArgumentException.ThrowIfNullOrEmpty(contentType);
        ArgumentNullException.ThrowIfNull(serializer);

        // Primary registration by the caller-specified content type.
        _serializers[contentType] = serializer;

        // Also fan out to all media-type aliases the serializer declares, so a single
        // Register(...) call covers structured-suffix variants (e.g. "application/json"
        // + "application/problem+json" if both are claimed by the instance).
        foreach (var alias in serializer.MediaTypes)
        {
            if (!string.IsNullOrEmpty(alias) &&
                !string.Equals(alias, contentType, StringComparison.OrdinalIgnoreCase))
            {
                _serializers.TryAdd(alias, serializer);
            }
        }
    }

    /// <inheritdoc />
    public IMessageSerializer? ResolveProfile(string profileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(profileName);
        return _profiles.GetValueOrDefault(profileName);
    }

    /// <inheritdoc />
    public void RegisterProfile(string profileName, IMessageSerializer serializer)
    {
        ArgumentException.ThrowIfNullOrEmpty(profileName);
        ArgumentNullException.ThrowIfNull(serializer);
        _profiles[profileName] = serializer;
    }
}
