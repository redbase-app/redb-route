namespace redb.Route.Abstractions;

/// <summary>
/// Snapshot of an exchange currently being processed (in-flight).
/// </summary>
/// <param name="ExchangeId">The exchange.</param>
/// <param name="RouteId">The route it entered.</param>
/// <param name="StartedAt">When it entered the route (UTC).</param>
/// <param name="ManagedThreadId">The thread it entered on.</param>
/// <param name="FromEndpoint">The endpoint the route consumes from, when known.</param>
/// <param name="WaitingUntil">
/// Set while the engine holds the exchange on purpose (a redelivery delay of <c>onException</c>, <c>Retry</c> or the dead
/// letter channel, a <c>delay</c> step): when that wait ends (UTC). Null while the exchange is being processed.
/// </param>
/// <param name="LastWaitEndedAt">When the last such wait ended (UTC); null when the exchange has not waited.</param>
public record InflightExchange(
    string ExchangeId,
    string RouteId,
    DateTime StartedAt,
    int ManagedThreadId,
    string? FromEndpoint,
    DateTime? WaitingUntil = null,
    DateTime? LastWaitEndedAt = null)
{
    /// <summary>
    /// When the exchange's current stretch of work began: the end of its last planned wait, or its start. A monitor
    /// measures how long an exchange has been busy from here, so a redelivery after a delay is a fresh attempt.
    /// </summary>
    public DateTime BusySince => LastWaitEndedAt ?? StartedAt;
}
