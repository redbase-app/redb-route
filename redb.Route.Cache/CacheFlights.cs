using System.Collections.Concurrent;

namespace redb.Route.Cache;

/// <summary>
/// The computations in progress, one per store key: while one exchange computes a missing entry, the
/// others arriving for the same key wait for its result instead of each running the inner steps.
/// One instance per context, with a map per provider, so every cache node that shares a store shares
/// its flights as well. Other processes (and other contexts) compute on their own.
/// </summary>
internal sealed class CacheFlights
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CacheEntry?>> _memory = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CacheEntry?>> _distributed = new(StringComparer.Ordinal);

    /// <summary>The flights of the store that serves <paramref name="provider"/> in this context.</summary>
    public ConcurrentDictionary<string, TaskCompletionSource<CacheEntry?>> For(CacheProvider provider)
        => provider == CacheProvider.Distributed ? _distributed : _memory;
}
