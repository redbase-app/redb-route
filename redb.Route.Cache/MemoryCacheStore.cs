using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace redb.Route.Cache;

/// <summary>In-process store over <see cref="IMemoryCache"/>: entries are kept as objects, no serialization.</summary>
internal sealed class MemoryCacheStore : ICacheStore
{
    private readonly IMemoryCache _cache;
    // The keys this store wrote, each with the token of its latest write: IMemoryCache cannot enumerate,
    // so a region-wide clear works from this list. An entry's eviction callback takes the key off the
    // list only while the list still holds that entry's own token, and never for a replacement:
    // MemoryCache fires the callback of the entry it replaces too (EvictionReason.Replaced), on the
    // thread pool, after the new entry is in, and a plain remove there dropped the live key.
    private readonly ConcurrentDictionary<string, object> _keys = new(StringComparer.Ordinal);

    public MemoryCacheStore(IMemoryCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public ValueTask<CacheEntry?> GetAsync(string key, CancellationToken ct)
        => ValueTask.FromResult(_cache.TryGetValue(key, out CacheEntry? entry) ? entry : null);

    public ValueTask SetAsync(string key, CacheEntry entry, TimeSpan? ttl, TimeSpan? sliding, CancellationToken ct)
    {
        // Registered before the entry is committed (the Dispose of the using): a cache that rejects the
        // entry at once (SizeLimit) fires its callback then, and finds the token to take off.
        var token = new object();
        _keys[key] = token;

        using var cacheEntry = _cache.CreateEntry(key);
        cacheEntry.Value = entry;
        if (ttl is not null) cacheEntry.AbsoluteExpirationRelativeToNow = ttl;
        if (sliding is not null) cacheEntry.SlidingExpiration = sliding;
        // Always sized: a cache with a SizeLimit (ours via MaxEntries, or the host's own) rejects an
        // entry without a Size on every write; a cache without a limit ignores the value.
        cacheEntry.Size = 1;
        cacheEntry.RegisterPostEvictionCallback(static (k, _, reason, state) =>
        {
            if (reason == EvictionReason.Replaced) return;
            var (keys, ownToken) = ((ConcurrentDictionary<string, object> Keys, object Token))state!;
            keys.TryRemove(new KeyValuePair<string, object>((string)k, ownToken));
        }, (_keys, token));
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(string key, CancellationToken ct)
    {
        _cache.Remove(key);
        _keys.TryRemove(key, out _);
        return ValueTask.CompletedTask;
    }

    public ValueTask ClearAsync(string region, CancellationToken ct)
    {
        var prefix = CacheKeys.Prefix(region);
        foreach (var key in _keys.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            _cache.Remove(key);
            _keys.TryRemove(key, out _);
        }
        return ValueTask.CompletedTask;
    }
}
