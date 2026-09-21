using System.Runtime.ExceptionServices;
using redb.Route.Abstractions;

namespace redb.Route.Core;

/// <summary>
/// Consumer-side view of an exchange's terminal state. A route whose <c>OnException</c> has no
/// <c>Handled(true)</c> returns <b>normally</b> with the exception still on the exchange; for a
/// consumer that decides ack/nack by "did Process throw", that failure would be acknowledged.
/// </summary>
public static class ExchangeFailureExtensions
{
    /// <summary>
    /// Exchange property set by <c>.RollbackAll()</c>, Camel's <c>markRollbackOnly()</c>: the unit of work is rolled
    /// back without an exception.
    /// </summary>
    public const string RollbackOnlyPropertyKey = "RollbackOnly";

    /// <summary>Marks the unit of work for rollback: the enclosing transaction rolls back and the consumer does not acknowledge.</summary>
    public static void MarkRollbackOnly(this IExchange exchange) => exchange.Properties[RollbackOnlyPropertyKey] = true;

    /// <summary>Whether the route marked the unit of work for rollback (<c>.RollbackAll()</c>).</summary>
    public static bool IsRollbackOnly(this IExchange exchange) =>
        exchange.Properties.TryGetValue(RollbackOnlyPropertyKey, out var value) && value is true;

    /// <summary>
    /// Whether the unit of work did not succeed: an unhandled failure is left on the exchange, or the route marked it
    /// rollback-only. A consumer does not acknowledge such an exchange.
    /// </summary>
    public static bool EndedInFailure(this IExchange exchange) =>
        (exchange.Exception is not null && !exchange.ExceptionHandled) || exchange.IsRollbackOnly();

    /// <summary>
    /// Rethrows the exchange's unhandled failure (stack preserved) so the consumer's existing
    /// failure path — nack / rollback / no-ack — handles it exactly like a thrown failure.
    /// A handled failure (<see cref="IExchange.ExceptionHandled"/>) is a completed exchange and is left alone.
    /// A rollback-only exchange throws too: its work was rolled back, so the message must be delivered again.
    /// </summary>
    public static void ThrowIfUnhandledFailure(this IExchange exchange)
    {
        if (exchange.Exception is { } failure && !exchange.ExceptionHandled)
            ExceptionDispatchInfo.Capture(failure).Throw();

        if (exchange.IsRollbackOnly())
            throw RollbackOnlyFailure();
    }

    /// <summary>
    /// The failure an exchange that returned normally ended in, as <see cref="EndedInFailure"/> sees it: the unhandled
    /// exception left on it, or, for a rollback-only exchange, an exception saying so; <see langword="null"/> when it
    /// succeeded.
    /// </summary>
    internal static Exception? FailureOf(IExchange exchange)
    {
        if (exchange.Exception is { } failure && !exchange.ExceptionHandled)
            return failure;
        return exchange.IsRollbackOnly() ? RollbackOnlyFailure() : null;
    }

    private static InvalidOperationException RollbackOnlyFailure() => new(
        "The route marked the exchange rollback-only (.RollbackAll()): its work was rolled back, so the message " +
        "is not acknowledged and the broker will deliver it again.");
}
