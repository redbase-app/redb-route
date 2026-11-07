using System.Collections.Concurrent;
using redb.Route.Abstractions;

namespace redb.Route.As4.Signals;

/// <summary>
/// The receipts a node sent, kept in memory to answer duplicates with the same receipt. Every receipt is stored
/// and almost none is read again, so entries leave by age (<c>retention</c>) and by count (the oldest beyond
/// <c>capacity</c>) on every store, not on a read that never comes.
/// </summary>
internal sealed class RecentReceipts(TimeSpan retention, int capacity) : IClaimCheckRepository
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(string Key, Entry Entry)> _order = new();
    private readonly object _trim = new();

    public Task<string> Store(ReadOnlyMemory<byte> data, TimeSpan? ttl = null, CancellationToken ct = default) =>
        throw new NotSupportedException("Receipts are stored under the id of the message they answer.");

    public Task Store(string key, ReadOnlyMemory<byte> data, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var entry = new Entry(data.ToArray(), DateTimeOffset.UtcNow + (ttl ?? retention));
        _entries[key] = entry;
        _order.Enqueue((key, entry));
        Trim();
        return Task.CompletedTask;
    }

    public Task<byte[]?> Retrieve(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Task.FromResult(_entries.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow
            ? entry.Data
            : null);
    }

    public async Task<byte[]?> RetrieveAndRemove(string key, CancellationToken ct = default)
    {
        var data = await Retrieve(key, ct).ConfigureAwait(false);
        await Remove(key, ct).ConfigureAwait(false);
        return data;
    }

    public Task Remove(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        _entries.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    /// <summary>Drops from the oldest end what has expired or is beyond the capacity.</summary>
    private void Trim()
    {
        lock (_trim)
        {
            var now = DateTimeOffset.UtcNow;
            while (_order.TryPeek(out var oldest) && (oldest.Entry.ExpiresAt <= now || _order.Count > capacity))
            {
                _order.TryDequeue(out _);
                // Only the entry this queue item stored: a later Store under the same key has its own item.
                _entries.TryRemove(new KeyValuePair<string, Entry>(oldest.Key, oldest.Entry));
            }
        }
    }

    private sealed record Entry(byte[] Data, DateTimeOffset ExpiresAt);
}
