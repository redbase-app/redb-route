namespace redb.Route.Core;

/// <summary>
/// Base class for route configurations (Apache Camel's <c>RouteConfigurationBuilder</c>): error handling declared
/// once, apart from the routes, for the routes of every builder of the context. It is a route builder, so it is added
/// to a context the same way — <c>context.AddRoutes(...)</c>, or found by assembly discovery.
/// <example>
/// <code>
/// public sealed class IntegrationErrors : RouteConfigurationBuilder
/// {
///     protected override void Configuration()
///     {
///         // No id: the default, for every route that has no configurations assigned.
///         RouteConfiguration()
///             .OnException&lt;Exception&gt;()
///                 .MaximumRedeliveries(3)
///                 .Handled()
///                 .Log("failed: ${exception.message}");
///
///         // By id: for the routes that say .RouteConfigurationId("audit").
///         RouteConfiguration("audit")
///             .OnException&lt;IOException&gt;().Handled().To("direct://audit-failed");
///     }
/// }
///
/// context.AddRoutes(new IntegrationErrors());
/// </code>
/// </example>
/// </summary>
public abstract class RouteConfigurationBuilder : RouteBuilder
{
    /// <summary>Override this method to declare the configurations with <see cref="RouteBuilder.RouteConfiguration"/>.</summary>
    protected abstract void Configuration();

    /// <summary>Runs <see cref="Configuration"/>. A configuration builder may also define routes by overriding this and calling the base.</summary>
    protected override void Configure() => Configuration();
}
