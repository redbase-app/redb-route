using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace redb.Route.Cache;

/// <summary>In-process store over <see cref="IMemoryCache"/>: entries are kept as objects, no serialization.</summary>
internal sealed class MemoryCacheStore : ICacheStore
{
    private readonly IMemoryCache _cache;
    private readonly ConcurrentDictionary<string, byte> _keys = new(StringComparer.Ordinal);

    public MemoryCacheStore(IMemoryCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public ValueTask<CacheEntry?> GetAsync(string key, CancellationToken ct)
        => ValueTask.FromResult(_cache.TryGetValue(key, out CacheEntry? entry) ? entry : null);

    public ValueTask SetAsync(string key, CacheEntry entry, TimeSpan? ttl, TimeSpan? sliding, CancellationToken ct)
    {
        using var cacheEntry = _cache.CreateEntry(key);
        cacheEntry.Value = entry;
        if (ttl is not null) cacheEntry.AbsoluteExpirationRelativeToNow = ttl;
        if (sliding is not null) cacheEntry.SlidingExpiration = sliding;
        // Always sized: a cache with a SizeLimit (ours via MaxEntries, or the host's own) rejects an
        // entry without a Size on every write; a cache without a limit ignores the value.
        cacheEntry.Size = 1;
        cacheEntry.RegisterPostEvictionCallback(static (k, _, _, state) => ((ConcurrentDictionary<string, byte>)state!).TryRemove((string)k, out _), _keys);
        _keys[key] = 0;
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
