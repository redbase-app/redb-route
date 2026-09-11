using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using redb.Route.Abstractions;

namespace redb.Route.Cache;

/// <summary>
/// Store over <see cref="IDistributedCache"/>. Entries travel as a JSON envelope: text and bytes as
/// they are, any other body through the data-format registry (JSON by default) with its CLR type
/// name, so it comes back as the same type when that type is resolvable on the reading side.
/// Header values that are JSON primitives are kept; others are dropped.
/// </summary>
internal sealed class DistributedCacheStore : ICacheStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDistributedCache _cache;
    private readonly IDataFormatRegistry? _registry;
    // Keys this process wrote, with the moment they stop mattering (absolute TTL, else the sliding
    // window from the write, else never): the abstraction cannot enumerate, so region-wide clear works
    // from this list, and expired entries are swept periodically so the list does not grow forever.
    private readonly ConcurrentDictionary<string, DateTime> _writtenKeys = new(StringComparer.Ordinal);
    private int _writes;

    public DistributedCacheStore(IDistributedCache cache, IDataFormatRegistry? registry)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _registry = registry;
    }

    public async ValueTask<CacheEntry?> GetAsync(string key, CancellationToken ct)
    {
        var bytes = await _cache.GetAsync(key, ct).ConfigureAwait(false);
        if (bytes is null) return null;

        var envelope = JsonSerializer.Deserialize<Envelope>(bytes, JsonOptions);
        return envelope is null ? null : Unwrap(envelope);
    }

    public async ValueTask SetAsync(string key, CacheEntry entry, TimeSpan? ttl, TimeSpan? sliding, CancellationToken ct)
    {
        var envelope = Wrap(entry);
        var options = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl, SlidingExpiration = sliding };
        await _cache.SetAsync(key, JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions), options, ct).ConfigureAwait(false);

        var now = DateTime.UtcNow;
        _writtenKeys[key] = ttl is { } t ? now + t : sliding is { } s ? now + s : DateTime.MaxValue;
        if (Interlocked.Increment(ref _writes) % 256 == 0)
            SweepExpired(now);
    }

    public async ValueTask RemoveAsync(string key, CancellationToken ct)
    {
        await _cache.RemoveAsync(key, ct).ConfigureAwait(false);
        _writtenKeys.TryRemove(key, out _);
    }

    /// <summary>Best effort: <see cref="IDistributedCache"/> cannot enumerate, so only keys written by this process are removed.</summary>
    public async ValueTask ClearAsync(string region, CancellationToken ct)
    {
        var prefix = CacheKeys.Prefix(region);
        foreach (var key in _writtenKeys.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            await RemoveAsync(key, ct).ConfigureAwait(false);
    }

    private void SweepExpired(DateTime now)
    {
        foreach (var (key, expires) in _writtenKeys)
            if (expires < now)
                _writtenKeys.TryRemove(new KeyValuePair<string, DateTime>(key, expires));
    }

    /// <summary>
    /// Resolves a body type name read out of the cache against the assemblies already loaded: a name
    /// written by another process (or by anyone who can write to the cache) must never make this
    /// process load an assembly from disk.
    /// </summary>
    private static Type? ResolveLoadedType(string name)
        => Type.GetType(name,
            assemblyName => AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase)),
            typeResolver: null,
            throwOnError: false);

    private Envelope Wrap(CacheEntry entry)
    {
        var envelope = new Envelope { ContentType = entry.ContentType, FromOut = entry.FromOut };
        switch (entry.Body)
        {
            case null: break;
            case string text: envelope.Text = text; break;
            case byte[] bytes: envelope.Bytes = bytes; break;
            default:
                var contentType = entry.ContentType ?? "application/json";
                var serializer = _registry?.GetSerializer(contentType) ?? _registry?.GetSerializer("application/json")
                    ?? throw new InvalidOperationException($"Cache: no data format for '{contentType}' to serialize a {entry.Body.GetType().Name} body.");
                envelope.Bytes = SerializeViaReflection(serializer, entry.Body);
                envelope.BodyType = entry.Body.GetType().AssemblyQualifiedName;
                envelope.ContentType ??= serializer.ContentType;
                break;
        }

        if (entry.Headers is not null)
        {
            envelope.Headers = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in entry.Headers)
                if (value is null or string or bool or sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal)
                    envelope.Headers[name] = JsonSerializer.SerializeToElement(value, JsonOptions);
        }
        return envelope;
    }

    private CacheEntry? Unwrap(Envelope envelope)
    {
        object? body = null;
        if (envelope.Text is not null) body = envelope.Text;
        else if (envelope.Bytes is not null)
        {
            body = envelope.Bytes;
            if (envelope.BodyType is not null)
            {
                // An object was cached under a type this process cannot resolve (a different deployment
                // wrote it, or the type is gone): handing raw bytes to a step that expects the object would
                // only fail later and further away, so the entry counts as a miss and is recomputed.
                var type = ResolveLoadedType(envelope.BodyType);
                var serializer = type is null ? null : _registry?.GetSerializer(envelope.ContentType ?? "application/json");
                if (type is null || serializer is null)
                    return null;
                body = serializer.Deserialize(envelope.Bytes, type);
            }
        }

        Dictionary<string, object?>? headers = null;
        if (envelope.Headers is not null)
        {
            headers = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, element) in envelope.Headers)
                headers[name] = element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDecimal(),
                    JsonValueKind.Null => null,
                    _ => element.GetRawText(),
                };
        }

        return new CacheEntry { Body = body, ContentType = envelope.ContentType, Headers = headers, FromOut = envelope.FromOut };
    }

    private static byte[] SerializeViaReflection(IMessageSerializer serializer, object body)
    {
        var method = typeof(IMessageSerializer).GetMethod(nameof(IMessageSerializer.Serialize))!.MakeGenericMethod(body.GetType());
        return (byte[])method.Invoke(serializer, [body])!;
    }

    private sealed class Envelope
    {
        public string? ContentType { get; set; }
        public string? BodyType { get; set; }
        public string? Text { get; set; }
        public byte[]? Bytes { get; set; }
        public Dictionary<string, JsonElement>? Headers { get; set; }
        public bool FromOut { get; set; }
    }
}
