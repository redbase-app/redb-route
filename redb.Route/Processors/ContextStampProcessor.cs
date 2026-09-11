using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Processors;

/// <summary>
/// Outermost per-route wrapper that stamps <see cref="IExchange.Context"/> for the duration of the
/// route and restores the previous value on exit (METRICS_IN_ROUTE_PLAN, П1).
/// </summary>
/// <remarks>
/// Save/set/restore rather than set-once, so "current" means the route actually executing: inside
/// a <c>direct:</c> or <c>vm:</c> sub-route the exchange sees the inner route's context — Camel's
/// reading — and the caller sees its own again when the sub-route returns. An exchange that never
/// entered a route keeps <c>null</c>, including after it comes back from one.
/// </remarks>
internal sealed class ContextStampProcessor : IProcessor
{
    private readonly IProcessor _inner;
    private readonly IRouteContext _context;

    public ContextStampProcessor(IProcessor inner, IRouteContext context)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        if (exchange is not Exchange stampable)
        {
            await _inner.Process(exchange, ct).ConfigureAwait(false);
            return;
        }

        var previous = stampable.Context;
        stampable.Context = _context;
        try
        {
            await _inner.Process(exchange, ct).ConfigureAwait(false);
        }
        finally
        {
            stampable.Context = previous;
        }
    }
}
