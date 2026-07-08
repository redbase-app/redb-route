using System.Collections.Concurrent;
using redb.Route.Abstractions;

namespace redb.Route.Core;

/// <summary>
/// Thread-safe in-memory implementation of <see cref="IInflightRepository"/>.
/// Uses ConcurrentDictionary for O(1) register/unregister and per-route counting.
/// </summary>
public sealed class DefaultInflightRepository : IInflightRepository
{
    private readonly ConcurrentDictionary<string, InflightExchange> _entries = new();

    /// <inheritdoc />
    public void Register(InflightExchange entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entries.TryAdd(entry.ExchangeId, entry);
    }

    /// <inheritdoc />
    public void Unregister(string exchangeId)
    {
        _entries.TryRemove(exchangeId, out _);
    }

    /// <inheritdoc />
    public IReadOnlyList<InflightExchange> Browse()
        => _entries.Values.ToList();

    /// <inheritdoc />
    public IReadOnlyList<InflightExchange> Browse(string routeId)
        => _entries.Values.Where(e => e.RouteId == routeId).ToList();

    /// <inheritdoc />
    public int Count => _entries.Count;

    /// <inheritdoc />
    public int CountByRoute(string routeId)
        => _entries.Values.Count(e => e.RouteId == routeId);
}
