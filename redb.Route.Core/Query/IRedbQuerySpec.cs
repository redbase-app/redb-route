using redb.Core.Query;

namespace redb.Route.RedbCore.Query;

/// <summary>
/// The full-LINQ escape hatch of <c>RedbQuery</c>/<c>&lt;redbQuery&gt;</c>: a named
/// specification the route references by its registry name (<c>filter="#activeOrders"</c>)
/// when the condition string cannot say what the query needs — joins of conditions the
/// translator refuses, provider-specific operators, reusable business filters. Register the
/// instance in the context registry (a <c>&lt;bean&gt;</c> does it from markup).
/// </summary>
/// <typeparam name="TProps">The props type the specification filters.</typeparam>
public interface IRedbQuerySpec<TProps> where TProps : class, new()
{
    /// <summary>Applies the specification to the query and returns the narrowed query.</summary>
    IRedbQueryable<TProps> Apply(IRedbQueryable<TProps> query);
}
