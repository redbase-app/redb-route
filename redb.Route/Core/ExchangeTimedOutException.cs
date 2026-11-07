namespace redb.Route.Core;

/// <summary>
/// Thrown when exchange processing exceeds the configured timeout.
/// Can be caught in error handlers via <c>OnException&lt;ExchangeTimedOutException&gt;()</c>.
/// </summary>
public sealed class ExchangeTimedOutException : TimeoutException
{
    /// <summary>The route where the timeout occurred.</summary>
    public string RouteId { get; }

    /// <summary>The configured timeout that was exceeded.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>Actual elapsed time before cancellation.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>Creates a new instance.</summary>
    public ExchangeTimedOutException(string routeId, TimeSpan timeout, TimeSpan elapsed)
        : base($"Exchange processing timed out after {elapsed.TotalSeconds}s on route '{routeId}'.")
    {
        RouteId = routeId;
        Timeout = timeout;
        Elapsed = elapsed;
    }
}
