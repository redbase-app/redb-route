using System.Collections;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jsonata.Net.Native;
using Jsonata.Net.Native.Json;
using StjBridge = Jsonata.Net.Native.SystemTextJson.JsonataExtensions;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.JsonTransform;

/// <summary>A parsed JSONata specification plus the source it came from.</summary>
internal sealed record CompiledJsonTransform(TextSource Source, JsonataQuery Query);

/// <summary>
/// Compiles JSONata specifications once (cache keyed by source identity) and applies them per message.
/// Input: the body as JSON text, bytes, a System.Text.Json tree, or a POCO (serialized camelCase).
/// Bindings: <c>$headers</c> and <c>$properties</c> — the same names the payload templates use.
/// </summary>
internal static class JsonTransformEngine
{
    private static readonly ConcurrentDictionary<string, Lazy<CompiledJsonTransform>> Cache = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions PocoOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Drops every cached specification (tests).</summary>
    public static void ClearCache() => Cache.Clear();

    public static CompiledJsonTransform Compile(TextSource source, JsonTransformOptions options)
    {
        // Keyed by the resolved file (path, size, write time): the cache is process-wide, and a relative
        // name alone would make two base directories share one specification.
        var key = source.ResolveCacheKey(options.BaseDirectory);
        var lazy = Cache.GetOrAdd(key, _ => new Lazy<CompiledJsonTransform>(() => Parse(source, options), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return lazy.Value;
        }
        catch
        {
            Cache.TryRemove(key, out _);   // do not cache a failure
            throw;
        }
    }

    private static CompiledJsonTransform Parse(TextSource source, JsonTransformOptions options)
    {
        string text;
        try
        {
            text = source.Read(options.BaseDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new JsonTransformCompilationException(source.Name, ex.Message, ex);
        }

        try
        {
            return new CompiledJsonTransform(source, new JsonataQuery(text));
        }
        catch (JsonataException ex)
        {
            throw new JsonTransformCompilationException(source.Name, ex.Message, ex);
        }
    }

    /// <summary>Applies the specification to the exchange body; <c>null</c> when the result is undefined.</summary>
    public static JToken? Run(CompiledJsonTransform compiled, IExchange exchange)
    {
        JToken input;
        try
        {
            input = ToJToken(exchange.In.Body);
        }
        catch (Exception ex) when (ex is JsonataException or Jsonata.Net.Native.Json.JsonParseException or System.Text.Json.JsonException
                                       or FormatException or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException($"JSON transform '{compiled.Source.Name}': the body is not JSON ({ex.Message}).", ex);
        }
        var environment = new EvaluationEnvironment();
        environment.BindValue("headers", StjBridge.FromObjectViaSystemTextJson(Sanitize(exchange.In.Headers), PocoOptions));
        environment.BindValue("properties", StjBridge.FromObjectViaSystemTextJson(Sanitize(exchange.Properties), PocoOptions));

        JToken result;
        try
        {
            result = compiled.Query.Eval(input, environment);
        }
        catch (JsonataException ex)
        {
            throw new InvalidOperationException($"JSON transform '{compiled.Source.Name}' failed: {ex.Message}", ex);
        }
        return result.Type == JTokenType.Undefined ? null : result;
    }

    private static JToken ToJToken(object? body) => body switch
    {
        null => JToken.Parse("null"),
        JToken token => token,
        string text => JToken.Parse(text),
        byte[] bytes => JToken.Parse(System.Text.Encoding.UTF8.GetString(bytes)),
        JsonNode node => StjBridge.FromSystemTextJson(node),
        JsonElement element => StjBridge.FromSystemTextJson(element),
        JsonDocument document => StjBridge.FromSystemTextJson(document),
        Stream stream => JToken.Parse(ReadStream(stream)),
        _ => StjBridge.FromObjectViaSystemTextJson(body, PocoOptions),
    };

    /// <summary>Reads a stream body from its position and leaves it there (a later step may need it); the stream stays open — it belongs to the exchange.</summary>
    private static string ReadStream(Stream stream)
    {
        var position = stream.CanSeek ? stream.Position : -1;
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        var text = reader.ReadToEnd();
        if (position >= 0) stream.Position = position;
        return text;
    }

    /// <summary>
    /// Headers and properties may hold anything (streams, DI scopes under <c>__redb_scope:*</c>); the
    /// binding keeps JSON-friendly values as they are, recurses into dictionaries and lists, and
    /// falls back to <c>ToString()</c> for the rest.
    /// </summary>
    private static Dictionary<string, object?> Sanitize(IEnumerable<KeyValuePair<string, object?>> pairs)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            if (key.StartsWith("__", StringComparison.Ordinal)) continue;
            result[key] = SanitizeValue(value, depth: 0);
        }
        return result;
    }

    private static object? SanitizeValue(object? value, int depth) => value switch
    {
        null => null,
        string or bool or sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal => value,
        DateTime or DateTimeOffset or Guid or TimeSpan => value.ToString(),
        JsonNode node => node,
        _ when depth > 8 => value.ToString(),
        IDictionary dictionary => dictionary.Cast<DictionaryEntry>()
            .ToDictionary(e => e.Key?.ToString() ?? string.Empty, e => SanitizeValue(e.Value, depth + 1)),
        IEnumerable list => list.Cast<object?>().Select(v => SanitizeValue(v, depth + 1)).ToList(),
        _ => value.ToString(),
    };
}
