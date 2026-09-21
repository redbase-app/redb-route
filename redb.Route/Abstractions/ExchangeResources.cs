namespace redb.Route.Abstractions;

/// <summary>
/// Resources an exchange releases when it ends (Apache Camel: <c>exchange.getExchangeExtension().addOnCompletion(...)</c>).
/// A component that hands the route something still holding a connection — a streamed query result, say — registers it
/// here, so the connection is returned when the exchange is released: whether or not the route used it, and whatever became
/// of the body or header that carried it.
/// <para>
/// Registered resources live in the exchange's properties under <see cref="PropertyPrefix"/>. The exchange releases them in
/// <c>ReleaseScopes</c> (and so in <c>DisposeAsync</c>), most recent first and before its DI scopes; a failing release is
/// logged and does not stop the others. Copies of the exchange — child, linked child, clone, snapshot — do not inherit them:
/// a resource belongs to the exchange that registered it.
/// </para>
/// <para>
/// Work that depends on how the exchange ended — confirm on success, undo on failure — registers an
/// <see cref="IExchangeCompletion"/> with <see cref="OnCompletion"/> instead: it runs when the exchange's unit of work
/// ends, which for a consumed message is before the consumer acknowledges it or hands it back to the broker.
/// </para>
/// </summary>
public static class ExchangeResources
{
    /// <summary>Property key prefix under which registered resources are kept.</summary>
    public const string PropertyPrefix = "__redb_resource:";

    private const string ScopePrefix = "__redb_scope:";

    private static long _sequence;

    /// <summary>Registers <paramref name="resource"/> to be released when <paramref name="exchange"/> ends.</summary>
    public static void ReleaseWithExchange(IExchange exchange, IAsyncDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(resource);

        // A fixed-width sequence keeps registration order sortable, so release can run most recent first.
        exchange.Properties[PropertyPrefix + Interlocked.Increment(ref _sequence).ToString("D19")] = resource;
    }

    /// <summary>
    /// Moves every resource registered on <paramref name="from"/> to <paramref name="to"/>, so it is released when
    /// <paramref name="to"/> ends (Apache Camel: <c>handoverCompletions</c>). Enrich uses it: the body it merges into the
    /// original exchange may be a stream still holding a connection, and the resource exchange it came from is disposed at once.
    /// </summary>
    public static void HandOver(IExchange from, IExchange to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (ReferenceEquals(from, to))
            return;

        foreach (var key in from.Properties.Keys.Where(k => k.StartsWith(PropertyPrefix, StringComparison.Ordinal)).ToList())
        {
            to.Properties[key] = from.Properties[key];
            from.Properties.Remove(key);
        }
    }

    /// <summary>
    /// Registers <paramref name="completion"/> to run when the unit of work of <paramref name="exchange"/> ends (Apache
    /// Camel: <c>addOnCompletion</c> with a <c>Synchronization</c>): <see cref="IExchangeCompletion.OnComplete"/> when it
    /// succeeded, <see cref="IExchangeCompletion.OnFailure"/> when it did not.
    /// <para>
    /// The unit of work belongs to the outermost route the exchange entered and ends before that route returns to its
    /// consumer — so a completion has run before the message is acknowledged or handed back to the broker. A route called
    /// with the same exchange (<c>direct:</c>) shares it. An exchange that entered no route of its own — a Split or
    /// Multicast branch, the copy <c>.Threads()</c> continues on, an exchange processed outside any route — ends its unit of
    /// work when it is released, and its outcome is then read from the exchange. Copies do not inherit completions.
    /// </para>
    /// </summary>
    public static void OnCompletion(IExchange exchange, IExchangeCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(completion);

        if (!exchange.Properties.TryGetValue(ExchangeUnitOfWork.PropertyKey, out var value)
            || value is not ExchangeUnitOfWork unitOfWork)
        {
            unitOfWork = new ExchangeUnitOfWork(ownedByRoute: false);
            exchange.Properties[ExchangeUnitOfWork.PropertyKey] = unitOfWork;
        }
        unitOfWork.Add(completion);
    }

    /// <summary>
    /// Opens the unit of work of an exchange entering a route: a new one when the exchange has none, which the route then
    /// ends; <see langword="null"/> when one is already open — the route was called with the exchange of another.
    /// </summary>
    internal static ExchangeUnitOfWork? OpenUnitOfWork(IExchange exchange)
    {
        if (exchange.Properties.ContainsKey(ExchangeUnitOfWork.PropertyKey))
            return null;

        var unitOfWork = new ExchangeUnitOfWork(ownedByRoute: true);
        exchange.Properties[ExchangeUnitOfWork.PropertyKey] = unitOfWork;
        return unitOfWork;
    }

    /// <summary>The first completion of type <typeparamref name="T"/> registered on the exchange that matches.</summary>
    internal static T? FindCompletion<T>(IExchange exchange, Func<T, bool> match) where T : class, IExchangeCompletion =>
        exchange.Properties.TryGetValue(ExchangeUnitOfWork.PropertyKey, out var value) && value is ExchangeUnitOfWork unitOfWork
            ? unitOfWork.Find(match)
            : null;

    /// <summary>
    /// The property is the exchange's own bookkeeping — a cached DI scope, a registered resource, the unit of work — which
    /// copies do not inherit and property masks do not remove.
    /// </summary>
    internal static bool IsOwnedByExchange(string propertyKey) =>
        propertyKey.StartsWith(ScopePrefix, StringComparison.Ordinal)
        || propertyKey.StartsWith(PropertyPrefix, StringComparison.Ordinal)
        || propertyKey == ExchangeUnitOfWork.PropertyKey
        || propertyKey == ResumePointsPropertyKey;

    /// <summary>
    /// Where routing picks up after a continued failure (<c>Processors.ResumePoints</c>): the exchange's own bookkeeping,
    /// naming pipelines of the route it is running in, which a copy must not inherit.
    /// </summary>
    private const string ResumePointsPropertyKey = "__redb_resume";
}
