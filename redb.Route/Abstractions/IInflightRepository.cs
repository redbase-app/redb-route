namespace redb.Route.Abstractions;

/// <summary>
/// Tracks exchanges currently being processed (in-flight) across all routes.
/// Thread-safe. Used by watchdog/monitoring to detect hung exchanges.
/// </summary>
public interface IInflightRepository
{
    /// <summary>Registers an exchange as in-flight.</summary>
    void Register(InflightExchange entry);

    /// <summary>Removes an exchange from the in-flight registry.</summary>
    void Unregister(string exchangeId);

    /// <summary>Returns a snapshot of all in-flight exchanges.</summary>
    IReadOnlyList<InflightExchange> Browse();

    /// <summary>Returns a snapshot of in-flight exchanges for a specific route.</summary>
    IReadOnlyList<InflightExchange> Browse(string routeId);

    /// <summary>Total number of in-flight exchanges across all routes.</summary>
    int Count { get; }

    /// <summary>Number of in-flight exchanges for a specific route.</summary>
    int CountByRoute(string routeId);

    /// <summary>
    /// Marks an in-flight exchange as held by the engine on purpose for <paramref name="delay"/> (a redelivery delay, a
    /// <c>delay</c> step): <see cref="InflightExchange.WaitingUntil"/> is set until the returned handle is disposed, then
    /// <see cref="InflightExchange.LastWaitEndedAt"/>. A monitor does not take such an exchange for a hung one. The
    /// default does nothing, so a repository of one's own keeps working as before.
    /// </summary>
    IDisposable BeginWait(string exchangeId, TimeSpan delay) => NoWait.Instance;

    /// <summary>The handle of a wait nothing records.</summary>
    private sealed class NoWait : IDisposable
    {
        public static readonly NoWait Instance = new();
        public void Dispose() { }
    }
}
