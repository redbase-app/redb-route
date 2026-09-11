using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Telemetry;
using redb.Route.Transactions;

namespace redb.Route.Processors;

/// <summary>One compiled <c>OnCompletion</c> block.</summary>
internal sealed record CompletionHandler(IProcessor Body, CompletionMode Mode, IPredicate? Condition, bool BeforeConsumer);

/// <summary>
/// Runs <c>OnCompletion</c> blocks after the route finished with an exchange — on a copy, outside the
/// route's error handlers and transaction, never changing the route's outcome. Placement in the route
/// wrapper stack is outside every error handler, so "failure" means the exception is escaping the
/// route; a handled error is a completion. After-consumer blocks are fired and forgotten; a failing
/// block is logged, never thrown.
/// </summary>
internal sealed class OnCompletionProcessor : IProcessor
{
    private readonly IProcessor _route;
    private readonly IReadOnlyList<CompletionHandler> _handlers;
    private readonly string _routeId;
    private readonly ILogger? _logger;

    public OnCompletionProcessor(IProcessor route, IReadOnlyList<CompletionHandler> handlers, string routeId, ILogger? logger)
    {
        _route = route ?? throw new ArgumentNullException(nameof(route));
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
        _routeId = routeId;
        _logger = logger;
    }

    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        try
        {
            await _route.Process(exchange, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await RunHandlers(exchange, ex, ct).ConfigureAwait(false);
            throw;
        }
        await RunHandlers(exchange, null, ct).ConfigureAwait(false);
    }

    private async Task RunHandlers(IExchange exchange, Exception? failure, CancellationToken ct)
    {
        foreach (var handler in _handlers)
        {
            var applies = handler.Mode switch
            {
                CompletionMode.OnCompleteOnly => failure is null,
                CompletionMode.OnFailureOnly => failure is not null,
                _ => true,
            };
            if (!applies) continue;

            // Nothing in here may change the route's own outcome: a copy that cannot be made or a
            // condition that throws is the block's problem — logged and skipped, never the caller's.
            IExchange? copy = null;
            try
            {
                copy = exchange.Clone();
                if (failure is not null) copy.Exception = failure;
                // The copy runs outside the route's transaction and must not share its deferred
                // transport actions (same reasoning as WireTap): the original commits or rolls them back.
                copy.Properties.Remove(TransactedProcessor.TransactActionPropertyKey);

                if (handler.Condition is not null && !await handler.Condition.MatchesAsync(copy).ConfigureAwait(false))
                {
                    await Release(copy).ConfigureAwait(false);
                    continue;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "OnCompletion condition failed for route '{RouteId}'; the block is skipped", _routeId);
                if (copy is not null) await Release(copy).ConfigureAwait(false);
                continue;
            }

            if (handler.BeforeConsumer)
            {
                await Execute(handler, copy, ct).ConfigureAwait(false);
                continue;
            }

            // Detached, like WireTap: the caller's ambient transaction may still be open when the block
            // starts and will complete underneath it, so it is suppressed in the branch; telemetry is
            // re-rooted as a span linked to the originating trace. Captured here, entered in the branch.
            var origin = DetachedDispatch.Capture();
            _ = Task.Run(async () =>
            {
                using var frame = DetachedDispatch.Enter(origin, "redb.route.oncompletion");
                await Execute(handler, copy, CancellationToken.None).ConfigureAwait(false);
            }, CancellationToken.None);
        }
    }

    private async Task Execute(CompletionHandler handler, IExchange copy, CancellationToken ct)
    {
        try
        {
            await handler.Body.Process(copy, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "OnCompletion block failed for route '{RouteId}'", _routeId);
        }
        finally
        {
            await Release(copy).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The copy shares its body with the live exchange (<c>Message.Clone</c> copies the reference), so
    /// only the copy's DI scopes are released — never <c>DisposeAsync</c>, which would close the live
    /// body under the consumer. Same teardown as the Multicast / Splitter branch clones.
    /// </summary>
    private async ValueTask Release(IExchange copy)
    {
        try
        {
            if (copy is Exchange exchange) await exchange.ReleaseScopes().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "OnCompletion copy release failed for route '{RouteId}'", _routeId);
        }
    }
}
