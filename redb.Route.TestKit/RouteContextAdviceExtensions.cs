using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;

namespace redb.Route.TestKit;

/// <summary>
/// <c>AdviceWith</c> entry points. Advice runs in the window between <see cref="RouteContext.AddRoutes(RouteBuilder)"/>
/// and <see cref="RouteContext.Start"/>: the builders are configured once, the chosen definitions are
/// rewritten in place, and <c>Start()</c> compiles them as rewritten (it does not re-run <c>Configure()</c>).
/// </summary>
public static class RouteContextAdviceExtensions
{
    /// <summary>
    /// Rewrites the route with id <paramref name="routeId"/> (an explicit <c>RouteId("...")</c>, or the
    /// endpoint-derived id of an unnamed route) before the context starts.
    /// </summary>
    /// <exception cref="InvalidOperationException">The context is already started, or no route has that id.</exception>
    public static RouteContext AdviceRoute(this RouteContext context, string routeId, Action<AdviceWithBuilder> advice)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeId);
        ArgumentNullException.ThrowIfNull(advice);

        var routes = BuiltRoutes(context);
        var match = routes.FirstOrDefault(r => string.Equals(r.Id, routeId, StringComparison.OrdinalIgnoreCase));
        if (match.Definition is null)
            throw new InvalidOperationException(
                $"AdviceWith: no route with id '{routeId}'. Known routes: {string.Join(", ", routes.Select(r => $"'{r.Id}'"))}.");

        advice(new AdviceWithBuilder(match.Definition) { RouteId = match.Id });
        return context;
    }

    /// <summary>Applies the same advice to every route of the context (e.g. <c>a => a.MockEndpoints("kafka://*")</c>).</summary>
    public static RouteContext AdviceAllRoutes(this RouteContext context, Action<AdviceWithBuilder> advice)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(advice);

        foreach (var (id, definition) in BuiltRoutes(context))
            advice(new AdviceWithBuilder(definition) { RouteId = id });
        return context;
    }

    /// <summary>Configures the builders once (idempotent) and lists their routes with resolved ids.</summary>
    private static List<(string Id, RouteDefinition Definition)> BuiltRoutes(RouteContext context)
    {
        if (context.IsStarted)
            throw new InvalidOperationException("AdviceWith must run before RouteContext.Start(): routes are compiled from their definitions at start.");

        // Build every builder that has not been built yet (one added after an earlier advice included);
        // a built one keeps its definitions, and with them any advice already applied.
        foreach (var builder in context.RouteBuilders)
        {
            if (!builder.IsBuilt)
                builder.InternalBuild(context);
        }
        context.DefinitionsPrebuilt = true;

        var routes = new List<(string, RouteDefinition)>();
        foreach (var builder in context.RouteBuilders)
        {
            foreach (var definition in builder.Definitions)
            {
                var fromUri = definition.GetFromUri()
                    ?? throw new InvalidOperationException($"Route '{definition.GetRouteId() ?? "(unnamed)"}' has no From() endpoint.");
                // Same fallback the context uses when compiling an unnamed route.
                var id = definition.GetRouteId() ?? EndpointUri.Sanitize(EndpointUriParser.Parse(fromUri).NormalizedKey);
                routes.Add((id, definition));
            }
        }
        return routes;
    }
}
