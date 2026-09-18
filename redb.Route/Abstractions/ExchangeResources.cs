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
    /// The property is the exchange's own bookkeeping — a cached DI scope or a registered resource — which copies do not
    /// inherit and property masks do not remove.
    /// </summary>
    internal static bool IsOwnedByExchange(string propertyKey) =>
        propertyKey.StartsWith(ScopePrefix, StringComparison.Ordinal)
        || propertyKey.StartsWith(PropertyPrefix, StringComparison.Ordinal);
}
