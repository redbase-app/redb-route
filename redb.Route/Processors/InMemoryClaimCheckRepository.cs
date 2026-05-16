using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using redb.Route.Abstractions;

namespace redb.Route.Processors;

/// <summary>
/// In-memory implementation of <see cref="IClaimCheckRepository"/>.
/// Suitable for single-process scenarios, testing, and push/pop stash patterns.
/// Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </summary>
public sealed class InMemoryClaimCheckRepository : IClaimCheckRepository
{
    private readonly ConcurrentDictionary<string, Entry> _store = new(StringComparer.Ordinal);
    private readonly TimeSpan _defaultTtl;

    /// <summary>
    /// Creates a new in-memory claim check repository.
    /// </summary>
    /// <param name="defaultTtl">Default TTL for entries. Zero or null means no expiry.</param>
    public InMemoryClaimCheckRepository(TimeSpan? defaultTtl = null)
    {
        _defaultTtl = defaultTtl ?? TimeSpan.Zero;
    }

    /// <summary>Number of entries currently in the store (including potentially expired).</summary>
    public int Count => _store.Count;

    /// <inheritdoc />
    public Task<string> Store(ReadOnlyMemory<byte> data, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var key = Guid.NewGuid().ToString("N");
        StoreInternal(key, data, ttl);
        return Task.FromResult(key);
    }

    /// <inheritdoc />
    public Task Store(string key, ReadOnlyMemory<byte> data, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        StoreInternal(key, data, ttl);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<byte[]?> Retrieve(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (_store.TryGetValue(key, out var entry))
        {
            if (entry.IsExpired)
            {
                _store.TryRemove(key, out _);
                return Task.FromResult<byte[]?>(null);
            }

            return Task.FromResult<byte[]?>(entry.Data);
        }

        return Task.FromResult<byte[]?>(null);
    }

    /// <inheritdoc />
    public Task<byte[]?> RetrieveAndRemove(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (_store.TryRemove(key, out var entry) && !entry.IsExpired)
            return Task.FromResult<byte[]?>(entry.Data);

        return Task.FromResult<byte[]?>(null);
    }

    /// <inheritdoc />
    public Task Remove(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        _store.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    private void StoreInternal(string key, ReadOnlyMemory<byte> data, TimeSpan? ttl)
    {
        var effectiveTtl = ttl ?? _defaultTtl;
        var expiresAt = effectiveTtl > TimeSpan.Zero
            ? DateTimeOffset.UtcNow + effectiveTtl
            : (DateTimeOffset?)null;

        _store[key] = new Entry(data.ToArray(), expiresAt);
    }

    private sealed record Entry(byte[] Data, DateTimeOffset? ExpiresAt)
    {
        public bool IsExpired => ExpiresAt.HasValue && DateTimeOffset.UtcNow >= ExpiresAt.Value;
    }
}
