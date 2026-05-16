using Microsoft.Extensions.DependencyInjection;
using redb.Core;
using redb.Route.Abstractions;

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
        {
            if (exchange?.ServiceProvider?.GetService<IRedbService>() is { } perExchange)
                return perExchange;

            return context.GetRedbService();
        }

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
        // Prefer exchange-scoped IRedbService (per-exchange DI scope)
        if (exchange.ServiceProvider?.GetService<IRedbService>() is { } scoped)
            return scoped;

        // Fallback: route context singleton (backward compat)
        var context = route.GetContext()
            ?? throw new InvalidOperationException(
                "RouteContext is not available. Ensure the route builder has been configured.");

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
