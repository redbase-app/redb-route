#pragma warning disable CS0619
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Configuration;
using redb.Route.Core;

namespace redb.Route.Extensions;

/// <summary>
/// Extension methods for registering redb.Route in a Microsoft.Extensions.DependencyInjection container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the route context, hosted service, and route builders.
    /// <example>
    /// <code>
    /// services.AddRedbRoute(route =&gt;
    /// {
    ///     route.AddRouteBuilder&lt;MyRoutes&gt;();
    ///     route.AddComponent(new RedisComponent());
    /// });
    /// </code>
    /// </example>
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configure">Optional configuration action.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddRedbRoute(
        this IServiceCollection services,
        Action<RedbRouteBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Register context as singleton — one context per host
        services.TryAddSingleton(sp =>
        {
            var loggerFactory = sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>();
            var options = sp.GetService<Microsoft.Extensions.Options.IOptions<RouteEngineOptions>>()?.Value;
            return new RouteContext(loggerFactory: loggerFactory, options: options);
        });

        // Shared registry for cross-context direct-vm / vm components
        services.TryAddSingleton<SharedVmRegistry>();

        // Register IRouteContext as the same singleton
        services.TryAddSingleton<IRouteContext>(sp => sp.GetRequiredService<RouteContext>());

        // Register the hosted service
        services.AddHostedService<RouteHostedService>();

        // Apply user configuration
        if (configure != null)
        {
            var builder = new RedbRouteBuilder(services);
            configure(builder);
        }

        return services;
    }

    /// <summary>
    /// Registers a component as a singleton and hooks it into the route context through
    /// <see cref="IRouteContextConfigurator"/> — the hook <see cref="RouteHostedService"/>
    /// applies at startup. This is THE registration primitive for connector packages
    /// (<c>AddRedbRouteKafka()</c> and friends): a lazy marker singleton that nobody
    /// resolves never reaches the context.
    /// </summary>
    /// <typeparam name="TComponent">Concrete component type.</typeparam>
    public static IServiceCollection AddRouteComponent<TComponent>(this IServiceCollection services)
        where TComponent : class, IComponent
    {
        services.TryAddSingleton<TComponent>();
        services.AddSingleton<IRouteContextConfigurator>(sp =>
            new ComponentConfigurator<TComponent>(sp));
        return services;
    }

    /// <summary>
    /// Registers an arbitrary startup callback as an <see cref="IRouteContextConfigurator"/>.
    /// For connectors whose registration is more than a bare <c>AddComponent</c> — wiring a
    /// shared server manager, several components, named registry entries.
    /// </summary>
    public static IServiceCollection AddRouteContextConfigurator(
        this IServiceCollection services,
        Action<IServiceProvider, RouteContext> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        services.AddSingleton<IRouteContextConfigurator>(sp =>
            new DelegateContextConfigurator(sp, configure));
        return services;
    }
}

/// <summary>Applies a delegate against the context at startup (see <see cref="ServiceCollectionExtensions.AddRouteContextConfigurator"/>).</summary>
internal sealed class DelegateContextConfigurator : IRouteContextConfigurator
{
    private readonly IServiceProvider _sp;
    private readonly Action<IServiceProvider, RouteContext> _configure;

    public DelegateContextConfigurator(IServiceProvider sp, Action<IServiceProvider, RouteContext> configure)
    {
        _sp = sp;
        _configure = configure;
    }

    public void Configure(RouteContext context) => _configure(_sp, context);
}

/// <summary>
/// Fluent builder for configuring route context registration within DI.
/// </summary>
public sealed class RedbRouteBuilder
{
    private readonly IServiceCollection _services;

    /// <summary>Creates a new builder.</summary>
    internal RedbRouteBuilder(IServiceCollection services)
    {
        _services = services;
    }

    /// <summary>Gets the underlying service collection for advanced scenarios.</summary>
    public IServiceCollection Services => _services;

    /// <summary>
    /// Registers a <see cref="RouteBuilder"/> implementation.
    /// Instances are resolved from DI, so constructor injection works.
    /// </summary>
    /// <typeparam name="TBuilder">Concrete route builder type.</typeparam>
    /// <returns>This builder for chaining.</returns>
    public RedbRouteBuilder AddRouteBuilder<TBuilder>() where TBuilder : RouteBuilder
    {
        // Register the CONCRETE type (the configurator resolves TBuilder) and expose the SAME singleton as the
        // base RouteBuilder for enumeration. Registering only the base left GetRequiredService<TBuilder>()
        // unresolvable, so host startup threw "No service for type '…' has been registered".
        _services.AddSingleton<TBuilder>();
        _services.AddSingleton<RouteBuilder>(sp => sp.GetRequiredService<TBuilder>());

        // Hook up to context via a post-configure callback
        _services.AddSingleton<IRouteContextConfigurator>(sp =>
            new RouteBuilderConfigurator<TBuilder>(sp));

        return this;
    }

    /// <summary>
    /// Registers a component for a custom URI scheme.
    /// </summary>
    /// <typeparam name="TComponent">Concrete component type.</typeparam>
    /// <returns>This builder for chaining.</returns>
    public RedbRouteBuilder AddComponent<TComponent>() where TComponent : class, IComponent
    {
        // Same fix as AddRouteBuilder: the ComponentConfigurator resolves the concrete TComponent, so register
        // it directly and expose the same singleton as IComponent for enumeration.
        _services.AddSingleton<TComponent>();
        _services.AddSingleton<IComponent>(sp => sp.GetRequiredService<TComponent>());

        _services.AddSingleton<IRouteContextConfigurator>(sp =>
            new ComponentConfigurator<TComponent>(sp));

        return this;
    }

    /// <summary>
    /// Registers an inline route builder using an action.
    /// </summary>
    /// <param name="configure">Action to define routes inline.</param>
    /// <returns>This builder for chaining.</returns>
    public RedbRouteBuilder AddRoutes(Action<InlineRouteBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        _services.AddSingleton<IRouteContextConfigurator>(
            _ => new InlineRouteConfigurator(configure));

        return this;
    }
}

/// <summary>
/// Contract for context configuration callbacks applied before start.
/// Implementations are resolved from DI and applied by <see cref="RouteHostedService"/>.
/// </summary>
public interface IRouteContextConfigurator
{
    /// <summary>Applies configuration to the context.</summary>
    void Configure(RouteContext context);
}

internal sealed class RouteBuilderConfigurator<TBuilder> : IRouteContextConfigurator
    where TBuilder : RouteBuilder
{
    private readonly IServiceProvider _sp;

    public RouteBuilderConfigurator(IServiceProvider sp) => _sp = sp;

    public void Configure(RouteContext context)
    {
        var builder = _sp.GetRequiredService<TBuilder>();
        context.AddRoutes(builder);
    }
}

internal sealed class ComponentConfigurator<TComponent> : IRouteContextConfigurator
    where TComponent : class, IComponent
{
    private readonly IServiceProvider _sp;

    public ComponentConfigurator(IServiceProvider sp) => _sp = sp;

    public void Configure(RouteContext context)
    {
        var component = _sp.GetRequiredService<TComponent>();
        context.AddComponent(component);
    }
}

internal sealed class InlineRouteConfigurator : IRouteContextConfigurator
{
    private readonly Action<InlineRouteBuilder> _configure;

    public InlineRouteConfigurator(Action<InlineRouteBuilder> configure)
        => _configure = configure;

    public void Configure(RouteContext context) => context.AddRoutes(_configure);
}
