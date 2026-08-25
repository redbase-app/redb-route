using System.Collections.Concurrent;
using redb.Route.As2.Crypto;
using redb.Route.As2.Mdn;

namespace redb.Route.As2;

/// <summary>
/// Correlates asynchronous AS2 MDNs with the messages that requested them. A producer sending in async mode
/// registers the outgoing Message-ID and the MIC it expects back; when the partner later POSTs the MDN to
/// our receiver, it is matched here by <c>Original-Message-ID</c> and the waiter is completed. In-memory and
/// per-process — a durable, restart-surviving store is a post-MVP item (see docs/as2/05-ROADMAP.md).
/// </summary>
internal sealed class As2CorrelationStore : IDisposable
{
    private sealed record Pending(As2Mic ExpectedMic, DateTimeOffset RegisteredAt, TaskCompletionSource<MdnParser.MdnResult> Completion);

    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly Timer _sweepTimer;
    private readonly TimeSpan _maxAge;

    /// <param name="maxAge">Evict pending entries older than this (default 30 min).</param>
    /// <param name="sweepInterval">How often to run the sweep (default 5 min).</param>
    public As2CorrelationStore(TimeSpan? maxAge = null, TimeSpan? sweepInterval = null)
    {
        _maxAge = maxAge ?? TimeSpan.FromMinutes(30);
        var interval = sweepInterval ?? TimeSpan.FromMinutes(5);
        // Periodic eviction: without it a partner that never delivers a promised async MDN leaks one live
        // waiter per message forever (memory-exhaustion DoS).
        _sweepTimer = new Timer(static s => { try { ((As2CorrelationStore)s!).Sweep(((As2CorrelationStore)s!)._maxAge); } catch { } },
            this, interval, interval);
    }

    /// <inheritdoc />
    public void Dispose() => _sweepTimer.Dispose();

    /// <summary>Registers an outgoing message awaiting an async MDN. Returns a task that completes when the MDN arrives.</summary>
    public Task<MdnParser.MdnResult> Register(string messageId, As2Mic expectedMic)
    {
        var tcs = new TaskCompletionSource<MdnParser.MdnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[messageId] = new Pending(expectedMic, DateTimeOffset.UtcNow, tcs);
        return tcs.Task;
    }

    /// <summary>The MIC expected for a pending message, or <c>null</c> if unknown.</summary>
    public As2Mic? ExpectedMic(string messageId)
        => _pending.TryGetValue(messageId, out var pending) ? pending.ExpectedMic : null;

    /// <summary>Completes the waiter for <paramref name="messageId"/>. Returns false if there was no pending entry (orphan MDN).</summary>
    public bool Complete(string messageId, MdnParser.MdnResult result)
    {
        if (_pending.TryRemove(messageId, out var pending))
        {
            pending.Completion.TrySetResult(result);
            return true;
        }
        return false;
    }

    /// <summary>Faults and evicts pending entries older than <paramref name="maxAge"/> (orphan/timeout cleanup).</summary>
    public int Sweep(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        var removed = 0;
        foreach (var (key, pending) in _pending)
        {
            if (pending.RegisteredAt >= cutoff) continue;
            if (_pending.TryRemove(key, out var evicted))
            {
                evicted.Completion.TrySetException(new TimeoutException($"No async MDN received for message '{key}' within {maxAge}."));
                _ = evicted.Completion.Task.Exception; // observe: the waiter is usually discarded (async outcome
                                                       // arrives on the separate ReceiveMdn route), so faulting
                                                       // it would otherwise raise UnobservedTaskException at GC.
                removed++;
            }
        }
        return removed;
    }
}
