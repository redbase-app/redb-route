using redb.Route.Abstractions;
using redb.Route.Predicates;

namespace redb.Route.TestKit;

/// <summary>
/// Builds a <see cref="NotifyMatcher"/> that waits for route events instead of a mock at the end of
/// the route (Apache Camel <c>NotifyBuilder</c>): "route <c>orders</c> has processed 3 exchanges
/// without failure" is <c>ctx.Notify().FromRoute("orders").WhenDone(3).Create()</c>.
/// <para>
/// Scope (<see cref="FromRoute"/>, <see cref="From"/>, <see cref="Filter"/>) narrows which exchanges
/// are counted; the <c>When*</c> conditions are cumulative — the matcher matches when all of them hold.
/// </para>
/// </summary>
public sealed class NotifyBuilder
{
    private readonly IRouteContext _context;
    private readonly List<string> _routeIds = [];
    private readonly List<string> _fromMasks = [];
    private readonly List<Func<IExchange, Task<bool>>> _filters = [];
    private int? _received, _completed, _failed, _done;

    internal NotifyBuilder(IRouteContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>Counts only exchanges of the route with this id (repeatable).</summary>
    public NotifyBuilder FromRoute(string routeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeId);
        _routeIds.Add(routeId);
        return this;
    }

    /// <summary>Counts only exchanges of routes whose <c>From</c> URI matches the mask (<c>kafka://*</c>, <c>regex:...</c>).</summary>
    public NotifyBuilder From(string uriMask)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uriMask);
        _fromMasks.Add(uriMask);
        return this;
    }

    /// <summary>Counts only exchanges for which <paramref name="predicate"/> holds.</summary>
    public NotifyBuilder Filter(Func<IExchange, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _filters.Add(e => Task.FromResult(predicate(e)));
        return this;
    }

    /// <summary>Counts only exchanges for which <paramref name="predicate"/> holds (awaited).</summary>
    public NotifyBuilder Filter(IPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _filters.Add(predicate.MatchesAsync);
        return this;
    }

    /// <summary>Counts only exchanges for which the route-language condition holds (same compiler as <c>Filter(string)</c> in routes).</summary>
    public NotifyBuilder Filter(string condition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(condition);
        _filters.Add(PredicateFactory.FromString(condition).MatchesAsync);
        return this;
    }

    /// <summary>Matches once at least <paramref name="count"/> exchanges have entered the scoped routes.</summary>
    public NotifyBuilder WhenReceived(int count) { _received = Positive(count); return this; }

    /// <summary>Matches once at least <paramref name="count"/> exchanges completed without a propagated exception.</summary>
    public NotifyBuilder WhenCompleted(int count) { _completed = Positive(count); return this; }

    /// <summary>Matches once at least <paramref name="count"/> exchanges escaped their route with an exception.</summary>
    public NotifyBuilder WhenFailed(int count) { _failed = Positive(count); return this; }

    /// <summary>Matches once at least <paramref name="count"/> exchanges finished, completed or failed.</summary>
    public NotifyBuilder WhenDone(int count) { _done = Positive(count); return this; }

    /// <summary>Registers the matcher as a lifecycle listener on the context and starts counting.</summary>
    public NotifyMatcher Create()
    {
        if (_received is null && _completed is null && _failed is null && _done is null)
            throw new InvalidOperationException("NotifyBuilder needs at least one When* condition (WhenReceived / WhenCompleted / WhenFailed / WhenDone).");

        var matcher = new NotifyMatcher(_context, _routeIds.ToArray(), _fromMasks.ToArray(), _filters.ToArray(),
            _received, _completed, _failed, _done);
        _context.AddLifecycleListener(matcher);
        return matcher;
    }

    private static int Positive(int count) => count >= 1 ? count : throw new ArgumentOutOfRangeException(nameof(count), "Counts start at 1.");
}
