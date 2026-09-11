using System.Collections.Concurrent;
using redb.Route.Abstractions;

namespace redb.Route.Cache;

/// <summary>Runtime side of <see cref="CacheDefinition"/>.</summary>
internal sealed class CacheScopeProcessor : IProcessor
{
    /// <summary>Header set on every exchange passing a cache node: <c>true</c> on a hit, <c>false</c> on a miss.</summary>
    public const string HitHeader = "cache.hit";

    private readonly ICacheStore _store;
    private readonly Func<IExchange, string> _key;
    private readonly string _region;
    private readonly TimeSpan? _ttl;
    private readonly TimeSpan? _sliding;
    private readonly bool _cacheHeaders;
    private readonly IProcessor _inner;

    public CacheScopeProcessor(ICacheStore store, Func<IExchange, string> key, string region, TimeSpan? ttl, TimeSpan? sliding, bool cacheHeaders, IProcessor inner)
    {
        _store = store;
        _key = key;
        _region = region;
        _ttl = ttl;
        _sliding = sliding;
        _cacheHeaders = cacheHeaders;
        _inner = inner;
    }

    // Single flight per key: while one exchange computes a missing entry, the others arriving for the
    // same key wait for its result instead of each running the inner steps (a stampede on a hot key
    // the moment it expires is exactly what a cache is there to prevent).
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CacheEntry?>> _inFlight = new(StringComparer.Ordinal);

    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var key = _key(exchange);
        while (true)
        {
            var hit = await _store.GetAsync(key, ct).ConfigureAwait(false);
            if (hit is not null)
            {
                Apply(exchange, hit);
                return;
            }

            var mine = new TaskCompletionSource<CacheEntry?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var owner = _inFlight.GetOrAdd(key, mine);
            if (!ReferenceEquals(owner, mine))
            {
                // Somebody else is computing this key: take its result. Null means it failed or stopped
                // without caching; then look again, and compute here if nobody else has taken over.
                var computed = await owner.Task.WaitAsync(ct).ConfigureAwait(false);
                if (computed is not null)
                {
                    Apply(exchange, computed);
                    return;
                }
                continue;
            }

            try
            {
                var entry = await ComputeAsync(exchange, key, ct).ConfigureAwait(false);
                mine.TrySetResult(entry);
                return;
            }
            catch
            {
                mine.TrySetResult(null);
                throw;
            }
            finally
            {
                _inFlight.TryRemove(new KeyValuePair<string, TaskCompletionSource<CacheEntry?>>(key, mine));
            }
        }
    }

    private static void Apply(IExchange exchange, CacheEntry entry)
    {
        entry.ApplyTo(exchange);
        exchange.In.Headers[HitHeader] = true;
    }

    /// <summary>Runs the inner steps and stores the result; <c>null</c> when the exchange stopped or failed (nothing cached).</summary>
    private async Task<CacheEntry?> ComputeAsync(IExchange exchange, string key, CancellationToken ct)
    {
        exchange.In.Headers[HitHeader] = false;
        await _inner.Process(exchange, ct).ConfigureAwait(false);

        if (exchange.IsStopped || exchange.Exception is not null)
            return null;

        var fromOut = exchange.Out is not null;
        var result = fromOut ? exchange.Out! : exchange.In;
        var entry = CacheEntry.From(result, _cacheHeaders, fromOut);
        await _store.SetAsync(key, entry, _ttl, _sliding, ct).ConfigureAwait(false);
        return entry;
    }
}
