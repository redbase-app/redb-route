using System.Collections.Concurrent;
using redb.Route.Abstractions;

namespace redb.Route.Core;

/// <summary>
/// Thread-safe in-memory implementation of <see cref="IInflightRepository"/>.
/// Uses ConcurrentDictionary for O(1) register/unregister and per-route counting.
/// </summary>
/// <remarks>
/// An exchange is registered once per entry into a route, and a copy keeps the exchange id: an exchange entering a
/// route again (a route calling itself, parallel branches through one sub-route) registers the same id more than once.
/// The entries are counted, so the exchange stays in flight until the last of them has finished; the entry shown is
/// the first one.
/// </remarks>
public sealed class DefaultInflightRepository : IInflightRepository
{
    private sealed record Tracked(InflightExchange Entry, int Entries);

    private readonly ConcurrentDictionary<string, Tracked> _entries = new();

    /// <inheritdoc />
    public void Register(InflightExchange entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entries.AddOrUpdate(entry.ExchangeId,
            static (_, added) => new Tracked(added, 1),
            static (_, tracked, _) => tracked with { Entries = tracked.Entries + 1 },
            entry);
    }

    /// <inheritdoc />
    public void Unregister(string exchangeId)
    {
        while (_entries.TryGetValue(exchangeId, out var tracked))
        {
            if (tracked.Entries <= 1)
            {
                if (_entries.TryRemove(new KeyValuePair<string, Tracked>(exchangeId, tracked)))
                    return;
            }
            else if (_entries.TryUpdate(exchangeId, tracked with { Entries = tracked.Entries - 1 }, tracked))
            {
                return;
            }
            // Raced with another entry of the same exchange: read it again.
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<InflightExchange> Browse()
        => _entries.Values.Select(t => t.Entry).ToList();

    /// <inheritdoc />
    public IReadOnlyList<InflightExchange> Browse(string routeId)
        => _entries.Values.Select(t => t.Entry).Where(e => e.RouteId == routeId).ToList();

    /// <inheritdoc />
    public int Count => _entries.Count;

    /// <inheritdoc />
    public int CountByRoute(string routeId)
        => _entries.Values.Count(t => t.Entry.RouteId == routeId);
}
