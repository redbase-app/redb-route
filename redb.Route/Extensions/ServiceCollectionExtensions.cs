#pragma warning disable CS0619
using System.Reflection;
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
    /// <exception cref="InvalidOperationException"><typeparamref name="TBuilder"/> was already added by <see cref="AddRouteBuilders(Assembly[])"/>.</exception>
    public RedbRouteBuilder AddRouteBuilder<TBuilder>() where TBuilder : RouteBuilder
    {
        if (IsDiscovered(typeof(TBuilder)))
            throw new InvalidOperationException(
                $"Route builder '{typeof(TBuilder).FullName}' was already added by AddRouteBuilders. " +
                "Leave it out of the discovery with the filter, or do not add it again.");

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
    /// Registers every route builder of <paramref name="assemblies"/> — each public, non-abstract, closed class
    /// deriving from <see cref="RouteBuilder"/> — as <see cref="AddRouteBuilder{TBuilder}"/> does, so constructor
    /// injection works (Apache Camel's routes discovery).
    /// </summary>
    /// <param name="assemblies">Assemblies to scan; at least one.</param>
    /// <returns>This builder for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// A builder type is already registered, or an assembly's types cannot all be loaded. Nothing is registered then.
    /// </exception>
    public RedbRouteBuilder AddRouteBuilders(params Assembly[] assemblies)
        => AddDiscoveredRouteBuilders(assemblies, filter: null);

    /// <summary>
    /// <see cref="AddRouteBuilders(Assembly[])"/> with a filter choosing which builders to take — the place for a
    /// condition (environment, configuration) and for leaving a builder out.
    /// </summary>
    /// <param name="assemblies">Assemblies to scan; at least one.</param>
    /// <param name="filter">Returns <c>true</c> for a builder type to register.</param>
    /// <returns>This builder for chaining.</returns>
    /// <exception cref="InvalidOperationException">As <see cref="AddRouteBuilders(Assembly[])"/>.</exception>
    public RedbRouteBuilder AddRouteBuilders(IEnumerable<Assembly> assemblies, Func<Type, bool> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return AddDiscoveredRouteBuilders(assemblies, filter);
    }

    private RedbRouteBuilder AddDiscoveredRouteBuilders(IEnumerable<Assembly> assemblies, Func<Type, bool>? filter)
    {
        var types = RouteBuilderDiscovery.FindBuilderTypes(assemblies, filter);

        // A type already registered would add its routes twice and fail only at host start with a duplicate route
        // id; said now, with the type's name. Checked for all before any is registered.
        foreach (var type in types)
        {
            if (_services.Any(d => d.ServiceType == type) || IsDiscovered(type))
                throw new InvalidOperationException(
                    $"Route builder '{type.FullName}' is already registered. " +
                    "Leave it out of the discovery with the filter, or do not register it explicitly.");
        }

        foreach (var type in types)
        {
            // The AddRouteBuilder<T> shape: the concrete type, the same singleton as RouteBuilder, a configurator
            // that adds it to the context — the container builds it, so its constructor may take services.
            _services.AddSingleton(type);
            _services.AddSingleton<RouteBuilder>(sp => (RouteBuilder)sp.GetRequiredService(type));
            _services.AddSingleton<IRouteContextConfigurator>(sp => new DiscoveredRouteBuilderConfigurator(sp, type));
            _services.AddSingleton(new DiscoveredRouteBuilder(type));
        }

        return this;
    }

    private bool IsDiscovered(Type builderType)
        => _services.Any(d => d.ImplementationInstance is DiscoveredRouteBuilder discovered && discovered.BuilderType == builderType);

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

/// <summary>Adds a route builder registered by <see cref="RedbRouteBuilder.AddRouteBuilders(Assembly[])"/> to the context.</summary>
internal sealed class DiscoveredRouteBuilderConfigurator : IRouteContextConfigurator
{
    private readonly IServiceProvider _sp;
    private readonly Type _builderType;

    public DiscoveredRouteBuilderConfigurator(IServiceProvider sp, Type builderType)
    {
        _sp = sp;
        _builderType = builderType;
    }

    public void Configure(RouteContext context)
        => context.AddRoutes((RouteBuilder)_sp.GetRequiredService(_builderType));
}

/// <summary>Marks a route builder type registered by assembly discovery, so a second registration of it is refused.</summary>
internal sealed record DiscoveredRouteBuilder(Type BuilderType);

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
