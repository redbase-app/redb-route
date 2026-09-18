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
    /// Rethrows the exchange's unhandled failure (stack preserved) so the consumer's existing
    /// failure path — nack / rollback / no-ack — handles it exactly like a thrown failure.
    /// A handled failure (<see cref="IExchange.ExceptionHandled"/>) is a completed exchange and is left alone.
    /// </summary>
    public static void ThrowIfUnhandledFailure(this IExchange exchange)
    {
        if (exchange.Exception is { } failure && !exchange.ExceptionHandled)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
