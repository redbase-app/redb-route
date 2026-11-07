using redb.Route.Abstractions;

namespace redb.Route.ErrorHandling;

/// <summary>
/// The headers a dead-lettered exchange carries (Apache Camel's <c>CamelDeadLetter*</c> family): why, what, when and
/// after how many redeliveries. One place for every dead letter channel of the engine.
/// </summary>
internal static class DeadLetterHeaders
{
    /// <summary>Stamps <paramref name="exchange"/> with <paramref name="failure"/> before it goes to a dead letter endpoint.</summary>
    /// <param name="exchange">The failed exchange.</param>
    /// <param name="failure">What failed it.</param>
    /// <param name="redeliveries">Redeliveries made before giving up; not written when zero.</param>
    internal static void Stamp(IExchange exchange, Exception failure, int redeliveries)
    {
        exchange.In.Headers["CamelDeadLetterReason"] = failure.Message;
        exchange.In.Headers["CamelDeadLetterExceptionType"] = failure.GetType().FullName;
        exchange.In.Headers["CamelDeadLetterTimestamp"] = DateTimeOffset.UtcNow;
        if (redeliveries > 0)
            exchange.In.Headers["CamelDeadLetterRedeliveryCount"] = redeliveries;
    }
}
