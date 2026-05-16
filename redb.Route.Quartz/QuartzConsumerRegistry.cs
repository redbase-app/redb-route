using System.Collections.Concurrent;

namespace redb.Route.Quartz;

/// <summary>
/// Thread-safe registry mapping endpoint URI keys to running Quartz consumers.
/// Allows <see cref="QuartzRouteJob"/> to find the correct consumer instance
/// (with its semaphore and processor pipeline) during job execution.
/// </summary>
internal static class QuartzConsumerRegistry
{
    private static readonly ConcurrentDictionary<string, QuartzConsumerBase> Consumers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers a consumer for the given endpoint URI key.</summary>
    internal static void Register(string endpointUriKey, QuartzConsumerBase consumer)
    {
        Consumers[endpointUriKey] = consumer;
    }

    /// <summary>Removes the consumer registration for the given endpoint URI key.</summary>
    internal static void Unregister(string endpointUriKey)
    {
        Consumers.TryRemove(endpointUriKey, out _);
    }

    /// <summary>Gets the consumer for the given endpoint URI key, or null if not found.</summary>
    internal static QuartzConsumerBase? Get(string endpointUriKey)
    {
        Consumers.TryGetValue(endpointUriKey, out var consumer);
        return consumer;
    }
}
