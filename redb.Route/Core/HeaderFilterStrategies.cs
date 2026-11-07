using redb.Route.Abstractions;

namespace redb.Route.Core;

/// <summary>
/// What a transport needs to honour the <c>headerFilterStrategy</c> option of its endpoints.
/// </summary>
public static class HeaderFilterStrategies
{
    /// <summary>
    /// The strategy an endpoint names by registry reference (<c>headerFilterStrategy=#name</c>; the <c>#</c> is
    /// optional), or <c>null</c> when the endpoint names none and the transport's default applies. A name the
    /// context registry does not hold as an <see cref="IHeaderFilterStrategy"/> is an error, raised where the endpoint
    /// is built: an endpoint must not run with a filter other than the one it asked for.
    /// </summary>
    /// <param name="context">The context whose registry holds the strategy.</param>
    /// <param name="reference">The option value.</param>
    /// <param name="endpoint">The endpoint, as the error names it.</param>
    public static IHeaderFilterStrategy? Resolve(IRouteContext? context, string? reference, string endpoint)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return null;

        var name = reference.Trim().TrimStart('#');
        if (context is null)
            throw new InvalidOperationException(
                $"{endpoint}: headerFilterStrategy '#{name}' cannot be resolved, the endpoint has no route context.");
        return context.GetFromRegistry<IHeaderFilterStrategy>(name)
               ?? throw new InvalidOperationException(
                   $"{endpoint}: headerFilterStrategy '#{name}' is not an IHeaderFilterStrategy in the context registry. " +
                   "Register it with AddToRegistry (or a <bean> in markup) before the route starts.");
    }
}
