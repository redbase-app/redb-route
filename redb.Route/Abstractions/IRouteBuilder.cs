namespace redb.Route.Abstractions;

/// <summary>
/// Configures routes using fluent DSL. Implement this to define route topology.
/// </summary>
public interface IRouteBuilder
{
    /// <summary>Configures routes on the given context.</summary>
    /// <param name="context">The route context to configure.</param>
    void Configure(IRouteContext context);
}
