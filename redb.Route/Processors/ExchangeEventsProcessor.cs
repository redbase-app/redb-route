using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Processors;

/// <summary>
/// Outermost per-route wrapper that publishes exchange-level lifecycle events
/// (<see cref="IRouteLifecycleListener.OnExchangeReceived"/>, <see cref="IRouteLifecycleListener.OnExchangeCompleted"/>,
/// <see cref="IRouteLifecycleListener.OnExchangeFailed"/>) to the context's listener set.
/// <para>
/// Sits outside inflight tracking and every error handler and reaches the consumer's verdict: "failed"
/// means an exception is about to reach the consumer, or the route returned with a failure left
/// unhandled on the exchange or a rollback-only mark; "completed" means the consumer acknowledges.
/// When no listener is registered the wrapper is a plain pass-through with no awaits added.
/// </para>
/// </summary>
internal sealed class ExchangeEventsProcessor : IProcessor
{
    private readonly IProcessor _inner;
    private readonly RouteContext _context;
    private readonly string _routeId;

    public ExchangeEventsProcessor(IProcessor inner, RouteContext context, string routeId)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _routeId = routeId ?? throw new ArgumentNullException(nameof(routeId));
    }

    public Task Process(IExchange exchange, CancellationToken ct = default)
        => _context.HasLifecycleListeners
            ? ProcessWithEvents(exchange, ct)
            : _inner.Process(exchange, ct);

    private async Task ProcessWithEvents(IExchange exchange, CancellationToken ct)
    {
        await _context.NotifyExchangeReceived(_routeId, exchange, ct).ConfigureAwait(false);
        try
        {
            await _inner.Process(exchange, ct).ConfigureAwait(false);
        }
        // A cooperative cancellation is neither completed nor failed — the exchange was abandoned
        // by its caller (BR-10). The listener hears it arrive and then hears nothing, which is the
        // truthful account; "failed" would make NotifyBuilder count a closed dashboard tab as a
        // route failure. The filter lets the exception fly past without a notify.
        catch (Exception ex) when (!Core.StatisticsProcessor.IsCooperativeCancellation(ex, ct))
        {
            await _context.NotifyExchangeFailed(_routeId, exchange, ex, ct).ConfigureAwait(false);
            throw;
        }

        // A normal return with a failure left unhandled on the exchange (OnException without Handled) or a rollback-only
        // mark (.RollbackAll()) is a failure: the consumer does not acknowledge it.
        if (ExchangeFailureExtensions.FailureOf(exchange) is { } failure)
            await _context.NotifyExchangeFailed(_routeId, exchange, failure, ct).ConfigureAwait(false);
        else
            await _context.NotifyExchangeCompleted(_routeId, exchange, ct).ConfigureAwait(false);
    }
}
