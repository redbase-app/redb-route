using Microsoft.Extensions.DependencyInjection;
using redb.Core;
using redb.Route.Abstractions;
using redb.Route.Controllers;

namespace redb.Route.RedbCore.Extensions;

/// <summary>
/// Extension methods for accessing <see cref="IRedbService"/> from route pipeline.
/// Provides typed integration points: Process, SetBody, SetHeader with <c>(IRedbService, IExchange)</c>.
/// When exchange has a DI scope (<see cref="IExchange.ServiceProvider"/>), a scoped
/// <see cref="IRedbService"/> is resolved per exchange; otherwise falls back to the route context singleton.
/// </summary>
public static class RedbRouteExtensions
{
    // ── Context helpers ──────────────────────────────────────────────

    private const string RegistryPrefix = "redb:";
    private const string FactoryPrefix = "redb-factory:";
    private const string ScopeCachePrefix = "__redb_scope:";
    // Reserved cache key for the DEFAULT (unnamed) per-exchange redb scope. The '\0' suffix
    // can never collide with a named instance ("__redb_scope:{name}") and is still disposed by
    // Exchange.ReleaseScopes() (which drops every key starting with ScopeCachePrefix).
    private const string DefaultScopeKey = ScopeCachePrefix + "\0";

    /// <summary>
    /// Resolves the default <see cref="IRedbService"/> from context service locator → DI fallback.
    /// </summary>
    public static IRedbService GetRedbService(this IRouteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.GetService<IRedbService>()
            ?? context.GetServiceProvider()?.GetService(typeof(IRedbService)) as IRedbService
            ?? throw new InvalidOperationException(
                "IRedbService is not registered. Call services.AddRedb() / services.AddRedbPro(), "
                + "or register a named instance via context.RegisterRedbService(name, instance).");
    }

    /// <summary>
    /// Resolves a named <see cref="IRedbService"/>.
    /// <para>
    /// <b>Scoping model for named instances:</b>
    /// Named IRedbService is stateful (like EF DbContext) and must NOT be shared across
    /// concurrent exchanges. When <paramref name="exchange"/> is provided, a per-exchange
    /// scoped instance is created from the registered <see cref="IServiceScopeFactory"/>
    /// (placed in registry by TsakContextManager under <c>"redb-factory:{name}"</c> key).
    /// The scope is cached in <see cref="IExchange.Properties"/> and auto-disposed when
    /// the exchange completes (<see cref="IAsyncDisposable.DisposeAsync"/>).
    /// </para>
    /// <para>
    /// Resolution priority:
    /// <list type="number">
    ///   <item>Cached scope in exchange.Properties (same exchange, second call)</item>
    ///   <item>New scope from factory in registry (first call with exchange)</item>
    ///   <item>Singleton fallback from registry (manual RegisterRedbService / no exchange)</item>
    /// </list>
    /// </para>
    /// Falls back to the default (unnamed) service if the name is null or empty.
    /// </summary>
    /// <param name="context">Route context.</param>
    /// <param name="name">
    /// Registry name (e.g. <c>"my-db"</c> or <c>"#my-db"</c> — leading <c>#</c> is stripped automatically).
    /// </param>
    /// <param name="exchange">
    /// Current exchange for per-exchange scoping. When provided, the resolved service is
    /// tied to the exchange lifecycle (thread-safe). Pass <c>null</c> for singleton fallback
    /// (seed/diagnostics only — not safe under concurrency).
    /// </param>
    public static IRedbService GetRedbService(this IRouteContext context, string name, IExchange? exchange = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Even when no name is supplied, prefer the per-exchange ServiceProvider so the
        // resolved IRedbService is tied to the exchange's DI scope (per-request scope) and
        // not captured as a captive singleton from the root provider — which would force all
        // concurrent requests through a single NpgsqlConnection and surface as
        // "A command is already in progress" / "connection is already in state 'Copy'".
        if (string.IsNullOrEmpty(name))
            return ResolveScopedRedb(context, exchange);

        var cleanName = name.TrimStart('#');

        // When exchange is provided, prefer per-exchange scoped instance (thread-safe).
        if (exchange != null)
        {
            var cacheKey = ScopeCachePrefix + cleanName;

            // 1. Already cached in this exchange?
            if (exchange.Properties.TryGetValue(cacheKey, out var cached) && cached is IServiceScope cachedScope)
                return cachedScope.ServiceProvider.GetRequiredService<IRedbService>();

            // 2. Factory registered? Create a new scope and cache it.
            if (context.GetFromRegistry<IServiceScopeFactory>(FactoryPrefix + cleanName) is { } factory)
            {
                var scope = factory.CreateScope();
                exchange.Properties[cacheKey] = scope;
                return scope.ServiceProvider.GetRequiredService<IRedbService>();
            }
        }

        // 3. Singleton fallback (manual RegisterRedbService or no exchange).
        return context.GetFromRegistry<IRedbService>(RegistryPrefix + cleanName)
            ?? throw new InvalidOperationException(
                $"Named IRedbService '{name}' is not found in context registry. "
                + "Register it via context.RegisterRedbService(name, instance) or configure in Redb section.");
    }

    /// <summary>
    /// Registers a named <see cref="IRedbService"/> in the context registry.
    /// Business routes use this to provide their own redb instance (different DB, provider, etc.).
    /// </summary>
    /// <param name="context">Route context.</param>
    /// <param name="name">Unique name for this redb instance (e.g. <c>"orders-db"</c>).</param>
    /// <param name="redbService">The redb service instance to register.</param>
    public static IRouteContext RegisterRedbService(this IRouteContext context, string name, IRedbService redbService)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(redbService);
        context.AddToRegistry(RegistryPrefix + name.TrimStart('#'), redbService);
        return context;
    }

    /// <summary>
    /// Resolves a PER-REQUEST scoped <see cref="IRedbService"/> for a controller — its own DB
    /// connection, tied to the controller's current <see cref="RedbController.Exchange"/>.
    /// <para>
    /// Use this in controllers instead of <c>Context.GetRedbService()</c>: the latter resolves a
    /// captive singleton (one shared, non-thread-safe connection) from the root provider, which under
    /// concurrent requests surfaces as "A command is already in progress" / "Connection is busy".
    /// </para>
    /// </summary>
    public static IRedbService Redb(this RedbController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        return controller.Context.GetRedbService(string.Empty, controller.Exchange);
    }

    // ── Process with IRedbService ────────────────────────────────────

    /// <summary>
    /// Processes the exchange with access to <see cref="IRedbService"/> (async).
    /// Resolves a scoped service per exchange when DI scope is available.
    /// </summary>
    /// <param name="route">Route definition.</param>
    /// <param name="action">Async action receiving the redb service and exchange.</param>
    public static IRouteDefinition ProcessWithRedb(
        this IRouteDefinition route,
        Func<IRedbService, IExchange, CancellationToken, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        return route.Process(async (exchange, ct) =>
        {
            var redb = ResolveRedbService(route, exchange);
            await action(redb, exchange, ct).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Processes the exchange with access to <see cref="IRedbService"/> (sync).
    /// Resolves a scoped service per exchange when DI scope is available.
    /// </summary>
    /// <param name="route">Route definition.</param>
    /// <param name="action">Action receiving the redb service and exchange.</param>
    public static IRouteDefinition ProcessWithRedb(
        this IRouteDefinition route,
        Action<IRedbService, IExchange> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        return route.Process(exchange =>
        {
            var redb = ResolveRedbService(route, exchange);
            action(redb, exchange);
        });
    }

    // ── SetBody with IRedbService ────────────────────────────────────

    /// <summary>
    /// Sets the exchange body using <see cref="IRedbService"/>.
    /// Resolves a scoped service per exchange when DI scope is available.
    /// </summary>
    /// <param name="route">Route definition.</param>
    /// <param name="factory">Factory that produces the body from service and exchange.</param>
    public static IRouteDefinition SetBodyFromRedb(
        this IRouteDefinition route,
        Func<IRedbService, IExchange, object?> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        return route.SetBody(exchange =>
        {
            var redb = ResolveRedbService(route, exchange);
            return factory(redb, exchange);
        });
    }

    // ── SetHeader with IRedbService ──────────────────────────────────

    /// <summary>
    /// Sets a header using <see cref="IRedbService"/>.
    /// Resolves a scoped service per exchange when DI scope is available.
    /// </summary>
    /// <param name="route">Route definition.</param>
    /// <param name="headerName">Header key.</param>
    /// <param name="factory">Factory that produces the header value from service and exchange.</param>
    public static IRouteDefinition SetHeaderFromRedb(
        this IRouteDefinition route,
        string headerName,
        Func<IRedbService, IExchange, object?> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        return route.SetHeader(headerName, exchange =>
        {
            var redb = ResolveRedbService(route, exchange);
            return factory(redb, exchange);
        });
    }

    // ── Named Process with IRedbService ─────────────────────────────

    /// <summary>
    /// Processes the exchange with access to a named <see cref="IRedbService"/> (async).
    /// Resolves a per-exchange scoped service when a factory is registered; see
    /// <see cref="GetRedbService(IRouteContext, string, IExchange?)"/> for scoping details.
    /// </summary>
    /// <param name="route">Route definition.</param>
    /// <param name="name">Named redb instance (e.g. <c>"orders-db"</c>).</param>
    /// <param name="action">Async action receiving the redb service and exchange.</param>
    public static IRouteDefinition ProcessWithRedb(
        this IRouteDefinition route,
        string name,
        Func<IRedbService, IExchange, CancellationToken, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        return route.Process(async (exchange, ct) =>
        {
            var redb = ResolveNamedRedbService(route, exchange, name);
            await action(redb, exchange, ct).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Processes the exchange with access to a named <see cref="IRedbService"/> (sync).
    /// Resolves a per-exchange scoped service when a factory is registered; see
    /// <see cref="GetRedbService(IRouteContext, string, IExchange?)"/> for scoping details.
    /// </summary>
    /// <param name="route">Route definition.</param>
    /// <param name="name">Named redb instance (e.g. <c>"orders-db"</c>).</param>
    /// <param name="action">Action receiving the redb service and exchange.</param>
    public static IRouteDefinition ProcessWithRedb(
        this IRouteDefinition route,
        string name,
        Action<IRedbService, IExchange> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        return route.Process(exchange =>
        {
            var redb = ResolveNamedRedbService(route, exchange, name);
            action(redb, exchange);
        });
    }

    // ── Named SetBody with IRedbService ──────────────────────────────

    /// <summary>
    /// Sets the exchange body using a named <see cref="IRedbService"/>.
    /// Resolves a per-exchange scoped service when a factory is registered; see
    /// <see cref="GetRedbService(IRouteContext, string, IExchange?)"/> for scoping details.
    /// </summary>
    /// <param name="route">Route definition.</param>
    /// <param name="name">Named redb instance (e.g. <c>"orders-db"</c>).</param>
    /// <param name="factory">Factory that produces the body from service and exchange.</param>
    public static IRouteDefinition SetBodyFromRedb(
        this IRouteDefinition route,
        string name,
        Func<IRedbService, IExchange, object?> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        return route.SetBody(exchange =>
        {
            var redb = ResolveNamedRedbService(route, exchange, name);
            return factory(redb, exchange);
        });
    }

    // ── Named SetHeader with IRedbService ────────────────────────────

    /// <summary>
    /// Sets a header using a named <see cref="IRedbService"/>.
    /// Resolves a per-exchange scoped service when a factory is registered; see
    /// <see cref="GetRedbService(IRouteContext, string, IExchange?)"/> for scoping details.
    /// </summary>
    /// <param name="route">Route definition.</param>
    /// <param name="name">Named redb instance (e.g. <c>"orders-db"</c>).</param>
    /// <param name="headerName">Header key.</param>
    /// <param name="factory">Factory that produces the header value from service and exchange.</param>
    public static IRouteDefinition SetHeaderFromRedb(
        this IRouteDefinition route,
        string name,
        string headerName,
        Func<IRedbService, IExchange, object?> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        return route.SetHeader(headerName, exchange =>
        {
            var redb = ResolveNamedRedbService(route, exchange, name);
            return factory(redb, exchange);
        });
    }

    // ── Private helpers ──────────────────────────────────────────────

    private static IRedbService ResolveRedbService(IRouteDefinition route, IExchange exchange)
    {
        // Fast path: if the exchange's own DI scope already carries redb, return it WITHOUT
        // resolving the route context. GetContext() walks the Parent chain, so it must not run
        // when the exchange can already satisfy the request — a scopeless exchange is the only
        // case that actually needs the context fallback below.
        if (exchange?.ServiceProvider?.GetService<IRedbService>() is { } scoped)
            return scoped;

        var context = route.GetContext()
            ?? throw new InvalidOperationException(
                "RouteContext is not available. Ensure the route builder has been configured.");

        return ResolveScopedRedb(context, exchange);
    }

    /// <summary>
    /// Resolves the default (unnamed) <see cref="IRedbService"/> as a PER-EXCHANGE scoped instance
    /// (its own DB connection) — <b>never</b> a captive singleton resolved from the root provider.
    /// <para>
    /// <c>IRedbService</c> wraps a single, non-thread-safe DB connection (EF-DbContext model). Sharing
    /// one instance across concurrent exchanges/branches/requests surfaces as
    /// "A command is already in progress" / "Connection is busy". This resolver guarantees isolation:
    /// <list type="number">
    ///   <item>use the exchange's own DI scope if it already carries <c>IRedbService</c>;</item>
    ///   <item>otherwise create a dedicated scope from the context provider's
    ///     <see cref="IServiceScopeFactory"/> (redb is registered Scoped, so this yields a fresh
    ///     instance with its own connection), cached on the exchange and disposed by
    ///     <c>Exchange.ReleaseScopes()</c>;</item>
    ///   <item>only when there is NO exchange (seed/diagnostics — single-threaded bootstrap) fall back
    ///     to the unscoped singleton.</item>
    /// </list>
    /// </para>
    /// </summary>
    private static IRedbService ResolveScopedRedb(IRouteContext context, IExchange? exchange)
    {
        if (exchange != null)
        {
            // 1. Exchange's own scope already resolves redb.
            if (exchange.ServiceProvider?.GetService<IRedbService>() is { } scoped)
                return scoped;

            // 2. Dedicated scope cached from a previous call in this same exchange.
            if (exchange.Properties.TryGetValue(DefaultScopeKey, out var cached) && cached is IServiceScope cachedScope)
                return cachedScope.ServiceProvider.GetRequiredService<IRedbService>();

            // 3. Create a dedicated per-exchange scope from the context provider's scope factory.
            //    The very fact that the unscoped singleton resolves at all proves redb is registered
            //    in this provider — so a fresh scope from it yields a per-exchange instance + own connection.
            if (context.GetServiceProvider()?.GetService<IServiceScopeFactory>() is { } factory)
            {
                var scope = factory.CreateScope();
                if (scope.ServiceProvider.GetService<IRedbService>() is { } fromScope)
                {
                    exchange.Properties[DefaultScopeKey] = scope; // disposed by Exchange.ReleaseScopes()
                    return fromScope;
                }
                scope.Dispose(); // this provider has no default redb — fall through to the registry/singleton
            }
        }

        // No exchange (or no scoped redb available) — unscoped singleton. Safe only for
        // single-threaded seed/diagnostics; request/exchange paths never reach here.
        return context.GetRedbService();
    }

    /// <summary>
    /// Resolves a named IRedbService with per-exchange scoping.
    /// Uses the exchange to cache/create a scoped instance — see <see cref="GetRedbService(IRouteContext, string, IExchange?)"/>.
    /// </summary>
    private static IRedbService ResolveNamedRedbService(IRouteDefinition route, IExchange exchange, string name)
    {
        var context = route.GetContext()
            ?? throw new InvalidOperationException(
                "RouteContext is not available. Ensure the route builder has been configured.");

        return context.GetRedbService(name, exchange);
    }
}
