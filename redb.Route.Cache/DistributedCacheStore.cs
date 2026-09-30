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
/// Header values that are JSON primitives are kept; others are dropped. The envelope carries a
/// marker and a version: a value under our key without them is an error, never a hit or a miss.
/// </summary>
internal sealed class DistributedCacheStore : ICacheStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDistributedCache _cache;
    private readonly IDataFormatRegistry? _registry;
    // Keys this process wrote, each with the moment it stops mattering: the abstraction cannot enumerate,
    // so region-wide clear works from this list, and expired leases are swept periodically so the list
    // does not grow forever. A sliding entry lives on with every read, so a hit renews its lease too.
    private readonly ConcurrentDictionary<string, KeyLease> _writtenKeys = new(StringComparer.Ordinal);
    private int _writes;
    // Body types already resolved by name, so a hit does not walk the loaded assemblies again. Only
    // what resolved is remembered (a type may load later), and only for this store's context.
    private readonly ConcurrentDictionary<string, Type> _resolvedTypes = new(StringComparer.Ordinal);

    private readonly record struct KeyLease(DateTime Expires, DateTime? Absolute, TimeSpan? Sliding)
    {
        public static KeyLease Start(DateTime now, TimeSpan? ttl, TimeSpan? sliding)
        {
            DateTime? absolute = ttl is { } t ? now + t : null;
            return new KeyLease(Until(now, absolute, sliding), absolute, sliding);
        }

        public KeyLease Renewed(DateTime now) => this with { Expires = Until(now, Absolute, Sliding) };

        private static DateTime Until(DateTime now, DateTime? absolute, TimeSpan? sliding)
        {
            if (sliding is not { } s) return absolute ?? DateTime.MaxValue;
            var slid = now + s;
            return absolute is { } a && a < slid ? a : slid;
        }
    }

    public DistributedCacheStore(IDistributedCache cache, IDataFormatRegistry? registry)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _registry = registry;
    }

    public async ValueTask<CacheEntry?> GetAsync(string key, CancellationToken ct)
    {
        var bytes = await _cache.GetAsync(key, ct).ConfigureAwait(false);
        if (bytes is null) return null;

        if (_writtenKeys.TryGetValue(key, out var lease) && lease.Sliding is not null)
            _writtenKeys[key] = lease.Renewed(DateTime.UtcNow);

        // A value under our key that we did not write is neither a hit nor a miss: a hit would hand the
        // route a body that is not one, a miss would overwrite somebody else's data. It is a shared key
        // space (or an entry from before the marker), and that is for the operator to sort out.
        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(bytes, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw NotOurs(key, "the value is not JSON", ex);
        }
        if (envelope is null || envelope.Format != Format)
            throw NotOurs(key, "the value carries no redb.Route envelope marker", null);
        if (envelope.Version != FormatVersion)
            throw NotOurs(key, $"the envelope is version {envelope.Version}; this build reads version {FormatVersion}", null);
        return Unwrap(envelope);
    }

    private static InvalidOperationException NotOurs(string key, string reason, Exception? inner)
        => new($"Cache: the value under key '{key}' in the distributed cache is not a redb.Route cache entry ({reason}). " +
               "Another writer shares this key space (give this cache its own key prefix, e.g. RedisCacheOptions.InstanceName, " +
               "or another region), or the entry predates the envelope marker (clear the region or let it expire).", inner);

    public async ValueTask SetAsync(string key, CacheEntry entry, TimeSpan? ttl, TimeSpan? sliding, CancellationToken ct)
    {
        var envelope = Wrap(entry);
        var options = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl, SlidingExpiration = sliding };
        await _cache.SetAsync(key, JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions), options, ct).ConfigureAwait(false);

        var now = DateTime.UtcNow;
        _writtenKeys[key] = KeyLease.Start(now, ttl, sliding);
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
        foreach (var (key, lease) in _writtenKeys)
            if (lease.Expires < now)
                _writtenKeys.TryRemove(new KeyValuePair<string, KeyLease>(key, lease));
    }

    /// <summary>
    /// Resolves a body type name read out of the cache against the assemblies already loaded: a name
    /// written by another process (or by anyone who can write to the cache) must never make this
    /// process load an assembly from disk.
    /// </summary>
    private Type? ResolveLoadedType(string name)
    {
        if (_resolvedTypes.TryGetValue(name, out var known)) return known;
        var type = Type.GetType(name,
            assemblyName => AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase)),
            typeResolver: null,
            throwOnError: false);
        if (type is not null) _resolvedTypes[name] = type;
        return type;
    }

    private Envelope Wrap(CacheEntry entry)
    {
        var envelope = new Envelope { Format = Format, Version = FormatVersion, ContentType = entry.ContentType, FromOut = entry.FromOut };
        switch (entry.Body)
        {
            case null: break;
            case string text: envelope.Text = text; break;
            case byte[] bytes: envelope.Bytes = bytes; break;
            default:
                var (serialized, serializer) = CacheSerialization.Serialize(_registry, entry.Body, entry.ContentType);
                envelope.Bytes = serialized;
                envelope.BodyType = entry.Body.GetType().AssemblyQualifiedName;
                envelope.ContentType ??= serializer.ContentType;
                break;
        }

        envelope.Headers = WrapHeaders(entry.Headers);
        envelope.InHeaders = WrapHeaders(entry.InHeaders);
        envelope.Removed = entry.RemovedHeaders is { Count: > 0 } removed ? removed.ToArray() : null;
        return envelope;
    }

    /// <summary>Header values that are JSON primitives travel; any other value is dropped.</summary>
    private static Dictionary<string, JsonElement>? WrapHeaders(IReadOnlyDictionary<string, object?>? headers)
    {
        if (headers is null) return null;
        var wrapped = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
            if (value is null or string or bool or sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal)
                wrapped[name] = JsonSerializer.SerializeToElement(value, JsonOptions);
        return wrapped;
    }

    private static Dictionary<string, object?>? UnwrapHeaders(Dictionary<string, JsonElement>? headers)
    {
        if (headers is null) return null;
        var unwrapped = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, element) in headers)
            unwrapped[name] = element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDecimal(),
                JsonValueKind.Null => null,
                _ => element.GetRawText(),
            };
        return unwrapped;
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

        return new CacheEntry
        {
            Body = body,
            ContentType = envelope.ContentType,
            Headers = UnwrapHeaders(envelope.Headers),
            InHeaders = UnwrapHeaders(envelope.InHeaders),
            RemovedHeaders = envelope.Removed,
            FromOut = envelope.FromOut,
        };
    }

    /// <summary>Marker every envelope carries; a value without it was not written by this store.</summary>
    private const string Format = "redb.route.cache";

    /// <summary>Shape of the envelope; a reader accepts exactly the version it writes.</summary>
    private const int FormatVersion = 1;

    private sealed class Envelope
    {
        public string? Format { get; set; }
        public int Version { get; set; }
        public string? ContentType { get; set; }
        public string? BodyType { get; set; }
        public string? Text { get; set; }
        public byte[]? Bytes { get; set; }
        public Dictionary<string, JsonElement>? Headers { get; set; }
        public Dictionary<string, JsonElement>? InHeaders { get; set; }
        public string[]? Removed { get; set; }
        public bool FromOut { get; set; }
    }
}
