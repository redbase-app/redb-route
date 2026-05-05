using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using redb.Route.Abstractions;

namespace redb.Route.Serialization;

/// <summary>
/// JSON message serializer using System.Text.Json.
/// Thread-safe, singleton-friendly. Configurable via <see cref="JsonSerializerOptions"/>.
/// </summary>
public sealed class JsonMessageSerializer : IMessageSerializer
{
    private readonly JsonSerializerOptions _options;
    private readonly string _contentType;
    private readonly IReadOnlyCollection<string> _mediaTypes;

    /// <summary>
    /// Default options: camelCase, case-insensitive, ignore null values on write, UTF-8.
    /// Uses <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> so non-ASCII
    /// (Cyrillic, emoji, diacritics) and ASCII punctuation like <c>"</c> are emitted
    /// as-is in UTF-8 instead of being escaped to <c>\u0022</c>/<c>\u0410</c>.
    /// This is safe for HTTP API responses; only unsafe when embedding JSON inside
    /// HTML/JS inline — which message-serializer output never does.
    /// Exotic transports (e.g. JSONP, HTML-inline) can override via constructor.
    /// </summary>
    public static readonly JsonSerializerOptions DefaultOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Creates a JSON serializer with default options.</summary>
    public JsonMessageSerializer()
        : this(DefaultOptions, contentType: "application/json", mediaTypes: null)
    {
    }

    /// <summary>Creates a JSON serializer with custom options.</summary>
    /// <param name="options">JSON serializer options.</param>
    public JsonMessageSerializer(JsonSerializerOptions options)
        : this(options, contentType: "application/json", mediaTypes: null)
    {
    }

    /// <summary>
    /// Creates a JSON serializer with custom options, a specific primary content type,
    /// and an optional extended list of media-type aliases the instance also handles.
    /// </summary>
    /// <param name="options">JSON serializer options.</param>
    /// <param name="contentType">Primary content type reported by
    /// <see cref="ContentType"/> (e.g. <c>application/scim+json</c>).</param>
    /// <param name="mediaTypes">Optional alias list reported by
    /// <see cref="MediaTypes"/>. If <c>null</c>, defaults to <c>[contentType]</c>.</param>
    /// <remarks>
    /// Used to ship profile serializers (SCIM, Problem Details, …) that own their own
    /// <see cref="JsonSerializerOptions"/> and declare non-<c>application/json</c>
    /// media types without subclassing.
    /// </remarks>
    public JsonMessageSerializer(
        JsonSerializerOptions options,
        string contentType,
        IReadOnlyCollection<string>? mediaTypes)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _contentType = !string.IsNullOrEmpty(contentType)
            ? contentType
            : throw new ArgumentException("Content type must not be empty.", nameof(contentType));
        _mediaTypes = mediaTypes is { Count: > 0 } ? mediaTypes : new[] { _contentType };
    }

    /// <inheritdoc />
    public string ContentType => _contentType;

    /// <inheritdoc />
    public IReadOnlyCollection<string> MediaTypes => _mediaTypes;

    /// <inheritdoc />
    public byte[] Serialize<T>(T value)
    {
        return JsonSerializer.SerializeToUtf8Bytes(value, _options);
    }

    /// <inheritdoc />
    public T? Deserialize<T>(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return JsonSerializer.Deserialize<T>(data, _options);
    }

    /// <inheritdoc />
    public object? Deserialize(byte[] data, Type type)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(type);
        return JsonSerializer.Deserialize(data, type, _options);
    }
}
