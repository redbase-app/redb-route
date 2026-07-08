using System.Collections.Concurrent;
using redb.Route.Abstractions;

namespace redb.Route.Llm.Engine.Storage;

/// <summary>
/// Caches deterministic / read-only tool outputs by a content-hash key so
/// repeated calls inside the same run (or across runs, when persisted) skip
/// the underlying side effect entirely. Engine consults this BEFORE the
/// idempotency store — a cache hit is reported as a skipped invocation to
/// observers.
/// <para>
/// The optional <c>exchange</c> parameter on every method carries the route
/// pipeline's current exchange; REDB-backed implementations resolve a
/// per-exchange <see cref="redb.Core.IRedbService"/> through
/// <c>IRouteContext.GetRedbService(name, exchange)</c>. In-memory
/// implementations ignore it.
/// </para>
/// </summary>
public interface IToolCacheStore
{
    /// <summary>Returns the cached output for <paramref name="cacheKey"/>, or null on a miss.</summary>
    ValueTask<string?> GetAsync(string cacheKey, IExchange? exchange = null, CancellationToken ct = default);

    /// <summary>Stores the cached output and (optionally) sets a relative time-to-live.</summary>
    ValueTask SetAsync(string cacheKey, string outputJson, TimeSpan? ttl = null, IExchange? exchange = null, CancellationToken ct = default);

    /// <summary>Drops every entry — used by tests.</summary>
    ValueTask ClearAsync(IExchange? exchange = null, CancellationToken ct = default);
}

/// <summary>In-memory tool cache. TTL is honoured lazily — entries are dropped on read.</summary>
public sealed class InMemoryToolCacheStore : IToolCacheStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<string?> GetAsync(string cacheKey, IExchange? exchange = null, CancellationToken ct = default)
    {
        if (!_entries.TryGetValue(cacheKey, out var entry)) return ValueTask.FromResult<string?>(null);
        if (entry.ExpiresAtUtc is { } exp && exp <= DateTime.UtcNow)
        {
            _entries.TryRemove(cacheKey, out _);
            return ValueTask.FromResult<string?>(null);
        }
        return ValueTask.FromResult<string?>(entry.OutputJson);
    }

    /// <inheritdoc />
    public ValueTask SetAsync(string cacheKey, string outputJson, TimeSpan? ttl = null, IExchange? exchange = null, CancellationToken ct = default)
    {
        var expires = ttl is { } t && t > TimeSpan.Zero ? DateTime.UtcNow.Add(t) : (DateTime?)null;
        _entries[cacheKey] = new Entry(outputJson, expires);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask ClearAsync(IExchange? exchange = null, CancellationToken ct = default)
    {
        _entries.Clear();
        return ValueTask.CompletedTask;
    }

    private readonly record struct Entry(string OutputJson, DateTime? ExpiresAtUtc);
}
