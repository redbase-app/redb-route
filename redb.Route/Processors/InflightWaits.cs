using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Processors;

/// <summary>
/// Marks the planned waits of the engine (redelivery delays, the <c>delay</c> step) in the route context's in-flight
/// registry, so a monitor tells an exchange that waits by design from a hung one.
/// </summary>
internal static class InflightWaits
{
    /// <summary>Waits <paramref name="delay"/> with the exchange marked as waiting for that long.</summary>
    public static async Task DelayAsync(IExchange exchange, TimeSpan delay, CancellationToken ct)
    {
        using (Begin(exchange, delay))
            await Task.Delay(delay, ct).ConfigureAwait(false);
    }

    private static IDisposable? Begin(IExchange exchange, TimeSpan delay)
        => (exchange.Context as RouteContext)?.InflightRepository.BeginWait(exchange.ExchangeId, delay);
}
