using System.Collections.Concurrent;
using redb.Route.As2.Crypto;

namespace redb.Route.As2;

/// <summary>
/// Correlates asynchronous AS2 MDNs with the messages that requested them: a producer sending in async mode records
/// the outgoing Message-ID and the MIC it expects back; when the partner later POSTs the MDN to our receiver, the MIC
/// is looked up by <c>Original-Message-ID</c> and compared. Nothing waits on an entry — the verdict reaches the
/// <c>ReceiveMdn</c> route as its own exchange. In-memory and per-process: after a restart, past the TTL, or on another
/// node an MDN finds no entry and its MIC status is <c>unknown</c> (see <see cref="As2Headers.MdnMicStatus"/>).
/// </summary>
internal sealed class As2CorrelationStore : IDisposable
{
    private sealed record Pending(As2Mic ExpectedMic, DateTimeOffset RegisteredAt);

    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly Timer _sweepTimer;
    private readonly TimeSpan _maxAge;

    /// <param name="maxAge">Evict pending entries older than this (default 30 min).</param>
    /// <param name="sweepInterval">How often to run the sweep (default 5 min).</param>
    public As2CorrelationStore(TimeSpan? maxAge = null, TimeSpan? sweepInterval = null)
    {
        _maxAge = maxAge ?? TimeSpan.FromMinutes(30);
        var interval = sweepInterval ?? TimeSpan.FromMinutes(5);
        // Periodic eviction: a partner that never delivers a promised async MDN would otherwise leave one entry per
        // message forever.
        _sweepTimer = new Timer(static s => ((As2CorrelationStore)s!).Sweep(((As2CorrelationStore)s!)._maxAge),
            this, interval, interval);
    }

    /// <inheritdoc />
    public void Dispose() => _sweepTimer.Dispose();

    /// <summary>Records an outgoing message awaiting an async MDN, with the MIC the MDN must carry.</summary>
    public void Register(string messageId, As2Mic expectedMic) =>
        _pending[messageId] = new Pending(expectedMic, DateTimeOffset.UtcNow);

    /// <summary>The MIC expected for a pending message, or <c>null</c> if unknown.</summary>
    public As2Mic? ExpectedMic(string messageId)
        => _pending.TryGetValue(messageId, out var pending) ? pending.ExpectedMic : null;

    /// <summary>Removes the entry of a message whose MDN has been accepted. False if there was none.</summary>
    public bool Complete(string messageId) => _pending.TryRemove(messageId, out _);

    /// <summary>Evicts pending entries older than <paramref name="maxAge"/>; returns how many.</summary>
    public int Sweep(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        var removed = 0;
        foreach (var (key, pending) in _pending)
            if (pending.RegisteredAt < cutoff && _pending.TryRemove(key, out _))
                removed++;
        return removed;
    }
}
