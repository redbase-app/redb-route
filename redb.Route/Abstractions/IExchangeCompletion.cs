namespace redb.Route.Abstractions;

/// <summary>
/// Work attached to the outcome of an exchange's unit of work (Apache Camel: a <c>Synchronization</c> passed to
/// <c>addOnCompletion</c>). Register it with <see cref="ExchangeResources.OnCompletion"/>; exactly one of the two
/// methods runs, once, when the unit of work ends.
/// <para>
/// A failing completion is logged and does not change the exchange's outcome or stop the other completions. Both methods
/// receive <see cref="CancellationToken.None"/>: the outcome has to be recorded even for an exchange that was cancelled.
/// </para>
/// </summary>
public interface IExchangeCompletion
{
    /// <summary>The unit of work succeeded: the consumer acknowledges the message.</summary>
    Task OnComplete(IExchange exchange, CancellationToken ct);

    /// <summary>
    /// The unit of work did not succeed: an exception escaped the route, a failure was left unhandled on the exchange,
    /// or the route marked it rollback-only (<c>.RollbackAll()</c>). The consumer does not acknowledge the message.
    /// </summary>
    Task OnFailure(IExchange exchange, CancellationToken ct);
}
