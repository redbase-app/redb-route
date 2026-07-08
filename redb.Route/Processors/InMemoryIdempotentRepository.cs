using System.Collections.Concurrent;
using redb.Route.Abstractions;

namespace redb.Route.Processors;

/// <summary>
/// In-memory implementation of <see cref="IIdempotentRepository"/>.
/// Uses a <see cref="ConcurrentDictionary{TKey,TValue}"/> for thread-safe deduplication.
/// Supports optional TTL-based eviction to prevent unbounded memory growth.
/// Suitable for single-process scenarios and testing.
/// </summary>
public sealed class InMemoryIdempotentRepository : IIdempotentRepository
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _store = new(StringComparer.Ordinal);
    private readonly TimeSpan? _ttl;

    /// <summary>Creates a repository with optional TTL for automatic key expiration.</summary>
    /// <param name="ttl">
    /// Time-to-live for keys. Keys older than this are evicted on next <see cref="Add"/> call.
    /// Null means keys are kept indefinitely (default).
    /// </param>
    public InMemoryIdempotentRepository(TimeSpan? ttl = null)
    {
        _ttl = ttl;
    }

    /// <summary>Gets the current number of tracked keys (including potentially expired ones).</summary>
    public int Count => _store.Count;

    /// <inheritdoc />
    public Task<bool> Add(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        // Lazy eviction: remove expired entries on each Add
        if (_ttl.HasValue)
            EvictExpired();

        var added = _store.TryAdd(key, DateTimeOffset.UtcNow);
        return Task.FromResult(added);
    }

    /// <inheritdoc />
    public Task Confirm(string key, CancellationToken ct = default)
    {
        // No-op for in-memory: the key is already stored on Add
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Remove(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        _store.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> Contains(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (_ttl.HasValue && _store.TryGetValue(key, out var timestamp))
        {
            if (DateTimeOffset.UtcNow - timestamp > _ttl.Value)
            {
                _store.TryRemove(key, out _);
                return Task.FromResult(false);
            }
        }

        return Task.FromResult(_store.ContainsKey(key));
    }

    /// <inheritdoc />
    public Task Clear(CancellationToken ct = default)
    {
        _store.Clear();
        return Task.CompletedTask;
    }

    private void EvictExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - _ttl!.Value;
        foreach (var kvp in _store)
        {
            if (kvp.Value < cutoff)
                _store.TryRemove(kvp.Key, out _);
        }
    }
}
