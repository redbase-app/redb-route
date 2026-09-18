using System.Collections.Concurrent;
using redb.Route.Abstractions;

namespace redb.Route.Llm.Engine.Storage;

/// <summary>
/// Caches deterministic / read-only tool outputs by a content-hash key so repeated calls can skip
/// the underlying work: inside one run for <c>ToolCachingPolicy.Memoize</c> (served in process,
/// never through this store) and across runs for <c>ToolCachingPolicy.Persist</c>.
/// <para>
/// The agent loop consults this store for <c>Persist</c> tools, after approval and before the
/// idempotency store: a hit means "this exact input already produced this output", which is cheaper
/// than asking "was this tool_use already executed?", and it is reported to observers as a skipped
/// invocation with <c>SkipReason = "cache_hit"</c>. Only read-only tools may declare a caching
/// policy — a cached entry suppresses the side effect it stands for, so the route build rejects
/// <c>Caching != None</c> on a mutating tool.
/// </para>
/// <para>
/// An output the redaction filter changed is never stored: persisting the raw body would keep
/// redaction-bypassing content in the store, and persisting the redacted body would hand the model
/// different data on a hit than on a miss.
/// </para>
/// <para>
/// The optional <c>exchange</c> parameter on every method carries the route
/// pipeline's current exchange; REDB-backed implementations resolve a
/// per-exchange <see cref="redb.Core.IRedbService"/> through
/// <c>IRouteContext.GetRedbService(name, exchange)</c>. In-memory
/// implementations ignore it.
/// </para>
/// <para>
/// <b>A store does not report metrics.</b> Hit/miss counters belong to the agent loop, which is the
/// only layer that sees both this store and the run-scoped memo; a store that counted as well would
/// count every persisted read twice.
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
