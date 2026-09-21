using Microsoft.Extensions.Logging;

namespace redb.Route.Abstractions;

/// <summary>
/// An exchange's unit of work (Apache Camel: <c>UnitOfWork</c>): the completions registered with
/// <see cref="ExchangeResources.OnCompletion"/>, run once, in registration order, when it ends. The outermost route the
/// exchange entered opens and ends it (<see cref="OwnedByRoute"/>); otherwise the exchange's release ends it.
/// </summary>
internal sealed class ExchangeUnitOfWork
{
    /// <summary>Exchange property holding the unit of work. Owned by the exchange: copies do not inherit it.</summary>
    internal const string PropertyKey = "__redb_uow";

    private readonly object _gate = new();
    private List<IExchangeCompletion>? _completions;
    private bool _ended;

    internal ExchangeUnitOfWork(bool ownedByRoute) => OwnedByRoute = ownedByRoute;

    /// <summary>
    /// A route opened the unit of work and ends it before returning to its consumer; a release of the exchange in the
    /// middle of that route (an error handler calling <c>ReleaseScopes</c>) leaves it alone.
    /// </summary>
    internal bool OwnedByRoute { get; }

    internal void Add(IExchangeCompletion completion)
    {
        lock (_gate)
        {
            if (_ended)
                throw new InvalidOperationException(
                    "The exchange's unit of work has already ended: a completion registered now would never run.");
            (_completions ??= []).Add(completion);
        }
    }

    internal T? Find<T>(Func<T, bool> match) where T : class, IExchangeCompletion
    {
        lock (_gate)
            return _completions?.OfType<T>().FirstOrDefault(match);
    }

    /// <summary>
    /// Ends the unit of work: detaches it from the exchange (a later route entry with the same exchange opens a new one)
    /// and runs every completion. A failing completion is logged; the others still run and the outcome stands.
    /// </summary>
    internal async Task End(IExchange exchange, bool failed, ILogger? logger)
    {
        IExchangeCompletion[] completions;
        lock (_gate)
        {
            if (_ended)
                return;
            _ended = true;
            completions = _completions?.ToArray() ?? [];
        }

        if (exchange.Properties.TryGetValue(PropertyKey, out var current) && ReferenceEquals(current, this))
            exchange.Properties.Remove(PropertyKey);

        foreach (var completion in completions)
        {
            try
            {
                // Deliberately without the exchange's token: a cancelled exchange is a failed one, and its failure has to
                // be recorded, or the redelivery would pass for a duplicate.
                if (failed)
                    await completion.OnFailure(exchange, CancellationToken.None).ConfigureAwait(false);
                else
                    await completion.OnComplete(exchange, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex,
                    "Completion {Completion} failed on exchange {ExchangeId} (route '{RouteId}', outcome {Outcome}); the outcome stands and the remaining completions still run.",
                    completion.GetType().Name, exchange.ExchangeId, exchange.RouteId, failed ? "failure" : "success");
            }
        }
    }
}
