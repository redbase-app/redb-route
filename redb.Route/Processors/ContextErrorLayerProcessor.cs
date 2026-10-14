using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Processors;

/// <summary>
/// The outermost layer of a route's error scope (<see cref="RouteErrorScope"/>): the context's error handling for a
/// failure no <c>OnException</c> took — as Apache Camel's context error handler, which works only when no
/// <c>onException</c> matched. An <see cref="OnExceptionProcessor"/> that takes a failure returns (with
/// <c>Handled(false)</c> the failure stays on the exchange), one that does not take it rethrows; so this layer sees
/// exactly the failures nobody took.
/// <para>
/// The lookup is that of <see cref="IRouteContext.HandleException"/>: a local handler of the route, a global handler,
/// <see cref="IRouteContext.ErrorHandler"/>. A handler that marks the exchange handled (<see cref="IExchange.ExceptionHandled"/>
/// — a dead letter channel) ends the failure; otherwise it goes on to the caller, as with Camel's DefaultErrorHandler,
/// and so it does when nothing matched.
/// </para>
/// </summary>
internal sealed class ContextErrorLayerProcessor : IProcessor
{
    private readonly IProcessor _inner;
    private readonly RouteContext _context;
    private readonly string _routeId;

    internal ContextErrorLayerProcessor(IProcessor inner, RouteContext context, string routeId)
    {
        _inner = inner;
        _context = context;
        _routeId = routeId;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        try
        {
            await _inner.Process(exchange, ct).ConfigureAwait(false);
        }
        // A genuine cancellation is never handled; a downstream timeout is named a TimeoutException, as in OnException.
        catch (Exception ex) when (!RouteCancellation.IsCancellation(ex, ct))
        {
            // The route id is the compiled one: an exchange that went through a direct: route carries that route's id.
            var handled = await _context.HandleWithContextHandlers(exchange, RouteCancellation.Normalize(ex, ct), _routeId, ct)
                .ConfigureAwait(false);
            if (!handled || !exchange.ExceptionHandled)
                throw;
        }
    }
}
