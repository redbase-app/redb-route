using System.Diagnostics;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.TestKit;

/// <summary>
/// Counts exchange events (received / completed / failed) for the scope chosen in
/// <see cref="NotifyBuilder"/> and reports when the configured conditions hold.
/// Registered as an <see cref="IRouteLifecycleListener"/>; events arrive from the route's outermost
/// wrapper, so "completed" and "failed" reflect the final outcome after error handling.
/// </summary>
public sealed class NotifyMatcher : IRouteLifecycleListener, IDisposable
{
    private readonly IRouteContext _context;
    private readonly string[] _routeIds;
    private readonly string[] _fromMasks;
    private readonly Func<IExchange, Task<bool>>[] _filters;
    private readonly int? _received, _completed, _failed, _done;
    private int _receivedCount, _completedCount, _failedCount;
    private TaskCompletionSource<bool> _changed = NewSignal();
    private volatile bool _disposed;

    internal NotifyMatcher(IRouteContext context, string[] routeIds, string[] fromMasks, Func<IExchange, Task<bool>>[] filters,
        int? received, int? completed, int? failed, int? done)
    {
        _context = context;
        _routeIds = routeIds;
        _fromMasks = fromMasks;
        _filters = filters;
        _received = received;
        _completed = completed;
        _failed = failed;
        _done = done;
    }

    /// <summary>Exchanges that entered the scoped routes.</summary>
    public int ReceivedCount => Volatile.Read(ref _receivedCount);

    /// <summary>Exchanges that completed without a propagated exception.</summary>
    public int CompletedCount => Volatile.Read(ref _completedCount);

    /// <summary>Exchanges that escaped their route with an exception.</summary>
    public int FailedCount => Volatile.Read(ref _failedCount);

    /// <summary>Completed plus failed.</summary>
    public int DoneCount => CompletedCount + FailedCount;

    /// <summary><c>true</c> when every configured condition currently holds.</summary>
    public bool Matches()
        => (_received is null || ReceivedCount >= _received)
        && (_completed is null || CompletedCount >= _completed)
        && (_failed is null || FailedCount >= _failed)
        && (_done is null || DoneCount >= _done);

    /// <summary>Waits up to <paramref name="timeout"/> for the conditions to hold; <c>false</c> on timeout.</summary>
    public async Task<bool> MatchesAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var signal = Volatile.Read(ref _changed).Task;
            if (Matches()) return true;

            var remaining = timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) return false;

            await Task.WhenAny(signal, Task.Delay(remaining, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Zeroes the counters so the same matcher can be reused for the next batch.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _receivedCount, 0);
        Interlocked.Exchange(ref _completedCount, 0);
        Interlocked.Exchange(ref _failedCount, 0);
    }

    /// <summary>Stops counting. The listener registration stays with the context but ignores further events.</summary>
    public void Dispose() => _disposed = true;

    // ── IRouteLifecycleListener ──────────────────────────────────────────────

    async Task IRouteLifecycleListener.OnExchangeReceived(string routeId, IExchange exchange, CancellationToken ct)
    {
        if (await InScope(routeId, exchange).ConfigureAwait(false))
            Bump(ref _receivedCount);
    }

    async Task IRouteLifecycleListener.OnExchangeCompleted(string routeId, IExchange exchange, CancellationToken ct)
    {
        if (await InScope(routeId, exchange).ConfigureAwait(false))
            Bump(ref _completedCount);
    }

    async Task IRouteLifecycleListener.OnExchangeFailed(string routeId, IExchange exchange, Exception exception, CancellationToken ct)
    {
        if (await InScope(routeId, exchange).ConfigureAwait(false))
            Bump(ref _failedCount);
    }

    private async Task<bool> InScope(string routeId, IExchange exchange)
    {
        if (_disposed) return false;

        if (_routeIds.Length > 0 && !_routeIds.Contains(routeId, StringComparer.OrdinalIgnoreCase))
            return false;

        if (_fromMasks.Length > 0)
        {
            var route = _context.Routes.FirstOrDefault(r => string.Equals(r.RouteId, routeId, StringComparison.OrdinalIgnoreCase));
            if (route is null || !_fromMasks.Any(mask => UriMask.IsMatch(mask, route.FromUri)))
                return false;
        }

        foreach (var filter in _filters)
            if (!await filter(exchange).ConfigureAwait(false))
                return false;

        return true;
    }

    private void Bump(ref int counter)
    {
        Interlocked.Increment(ref counter);
        Interlocked.Exchange(ref _changed, NewSignal()).TrySetResult(true);
    }

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
