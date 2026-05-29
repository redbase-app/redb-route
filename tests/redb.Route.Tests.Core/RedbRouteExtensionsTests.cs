using Microsoft.Extensions.DependencyInjection;
using redb.Core;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.RedbCore.Extensions;

namespace redb.Route.Tests.Core;

/// <summary>
/// Tests for <see cref="RedbRouteExtensions"/>.
/// </summary>
public sealed class RedbRouteExtensionsTests
{
    // ── GetRedbService (from IRouteContext) ──────────────────────────

    [Fact]
    public void GetRedbService_RegisteredViaAddService_ReturnsIt()
    {
        var redb = Substitute.For<IRedbService>();
        var context = Substitute.For<IRouteContext>();
        context.GetService<IRedbService>().Returns(redb);

        var result = context.GetRedbService();

        result.Should().BeSameAs(redb);
    }

    [Fact]
    public void GetRedbService_RegisteredViaServiceProvider_ReturnsIt()
    {
        var redb = Substitute.For<IRedbService>();
        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(IRedbService)).Returns(redb);

        var context = Substitute.For<IRouteContext>();
        context.GetService<IRedbService>().Returns((IRedbService?)null);
        context.GetServiceProvider().Returns(sp);

        var result = context.GetRedbService();

        result.Should().BeSameAs(redb);
    }

    [Fact]
    public void GetRedbService_NotRegistered_Throws()
    {
        var context = Substitute.For<IRouteContext>();
        context.GetService<IRedbService>().Returns((IRedbService?)null);
        context.GetServiceProvider().Returns((IServiceProvider?)null);

        var act = () => context.GetRedbService();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*IRedbService*not registered*");
    }

    [Fact]
    public void GetRedbService_NullContext_Throws()
    {
        IRouteContext? context = null;

        var act = () => RedbRouteExtensions.GetRedbService(context!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("context");
    }

    // ── ProcessWithRedb (async) ─────────────────────────────────────

    [Fact]
    public void ProcessWithRedb_Async_NullAction_Throws()
    {
        var route = Substitute.For<IRouteDefinition>();

        var act = () => route.ProcessWithRedb((Func<IRedbService, IExchange, CancellationToken, Task>)null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("action");
    }

    [Fact]
    public void ProcessWithRedb_Async_ChainsToRoute()
    {
        var route = Substitute.For<IRouteDefinition>();
        route.Process(Arg.Any<Func<IExchange, CancellationToken, Task>>()).Returns(route);

        var result = route.ProcessWithRedb(async (redb, exchange, ct) =>
        {
            await Task.CompletedTask;
        });

        result.Should().BeSameAs(route);
        route.Received(1).Process(Arg.Any<Func<IExchange, CancellationToken, Task>>());
    }

    // ── ProcessWithRedb (sync) ──────────────────────────────────────

    [Fact]
    public void ProcessWithRedb_Sync_NullAction_Throws()
    {
        var route = Substitute.For<IRouteDefinition>();

        var act = () => route.ProcessWithRedb((Action<IRedbService, IExchange>)null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("action");
    }

    [Fact]
    public void ProcessWithRedb_Sync_ChainsToRoute()
    {
        var route = Substitute.For<IRouteDefinition>();
        route.Process(Arg.Any<Action<IExchange>>()).Returns(route);

        var result = route.ProcessWithRedb((redb, exchange) => { });

        result.Should().BeSameAs(route);
        route.Received(1).Process(Arg.Any<Action<IExchange>>());
    }

    // ── SetBodyFromRedb ─────────────────────────────────────────────

    [Fact]
    public void SetBodyFromRedb_NullFactory_Throws()
    {
        var route = Substitute.For<IRouteDefinition>();

        var act = () => route.SetBodyFromRedb(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("factory");
    }

    [Fact]
    public void SetBodyFromRedb_ChainsToRoute()
    {
        var route = Substitute.For<IRouteDefinition>();
        route.SetBody(Arg.Any<Func<IExchange, object?>>()).Returns(route);

        var result = route.SetBodyFromRedb((redb, exchange) => "value");

        result.Should().BeSameAs(route);
        route.Received(1).SetBody(Arg.Any<Func<IExchange, object?>>());
    }

    // ── SetHeaderFromRedb ───────────────────────────────────────────

    [Fact]
    public void SetHeaderFromRedb_NullFactory_Throws()
    {
        var route = Substitute.For<IRouteDefinition>();

        var act = () => route.SetHeaderFromRedb("X-Key", null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("factory");
    }

    [Fact]
    public void SetHeaderFromRedb_ChainsToRoute()
    {
        var route = Substitute.For<IRouteDefinition>();
        route.SetHeader(Arg.Any<string>(), Arg.Any<Func<IExchange, object?>>()).Returns(route);

        var result = route.SetHeaderFromRedb("X-Count", (redb, exchange) => 42);

        result.Should().BeSameAs(route);
        route.Received(1).SetHeader("X-Count", Arg.Any<Func<IExchange, object?>>());
    }

    // ── Scoped resolution via exchange.ServiceProvider ───────────────

    [Fact]
    public async Task ProcessWithRedb_Async_ResolvesFromExchangeScope()
    {
        var scopedRedb = Substitute.For<IRedbService>();
        var exchange = CreateExchangeWithScope(scopedRedb);

        Func<IExchange, CancellationToken, Task>? captured = null;
        var route = Substitute.For<IRouteDefinition>();
        route.Process(Arg.Any<Func<IExchange, CancellationToken, Task>>())
            .Returns(ci => { captured = ci.Arg<Func<IExchange, CancellationToken, Task>>(); return route; });

        route.ProcessWithRedb(async (redb, ex, ct) =>
        {
            redb.Should().BeSameAs(scopedRedb);
            await Task.CompletedTask;
        });

        captured.Should().NotBeNull();
        await captured!(exchange, CancellationToken.None);
    }

    [Fact]
    public void ProcessWithRedb_Sync_ResolvesFromExchangeScope()
    {
        var scopedRedb = Substitute.For<IRedbService>();
        var exchange = CreateExchangeWithScope(scopedRedb);

        Action<IExchange>? captured = null;
        var route = Substitute.For<IRouteDefinition>();
        route.Process(Arg.Any<Action<IExchange>>())
            .Returns(ci => { captured = ci.Arg<Action<IExchange>>(); return route; });

        route.ProcessWithRedb((redb, ex) =>
        {
            redb.Should().BeSameAs(scopedRedb);
        });

        captured.Should().NotBeNull();
        captured!(exchange);
    }

    [Fact]
    public void SetBodyFromRedb_ResolvesFromExchangeScope()
    {
        var scopedRedb = Substitute.For<IRedbService>();
        var exchange = CreateExchangeWithScope(scopedRedb);

        Func<IExchange, object?>? captured = null;
        var route = Substitute.For<IRouteDefinition>();
        route.SetBody(Arg.Any<Func<IExchange, object?>>())
            .Returns(ci => { captured = ci.Arg<Func<IExchange, object?>>(); return route; });

        route.SetBodyFromRedb((redb, ex) =>
        {
            redb.Should().BeSameAs(scopedRedb);
            return "ok";
        });

        captured.Should().NotBeNull();
        captured!(exchange).Should().Be("ok");
    }

    [Fact]
    public void SetHeaderFromRedb_ResolvesFromExchangeScope()
    {
        var scopedRedb = Substitute.For<IRedbService>();
        var exchange = CreateExchangeWithScope(scopedRedb);

        Func<IExchange, object?>? captured = null;
        var route = Substitute.For<IRouteDefinition>();
        route.SetHeader(Arg.Is("X-Key"), Arg.Any<Func<IExchange, object?>>())
            .Returns(ci => { captured = ci.Arg<Func<IExchange, object?>>(); return route; });

        route.SetHeaderFromRedb("X-Key", (redb, ex) =>
        {
            redb.Should().BeSameAs(scopedRedb);
            return 42;
        });

        captured.Should().NotBeNull();
        captured!(exchange).Should().Be(42);
    }

    [Fact]
    public async Task ProcessWithRedb_Async_FallsBackToRouteContext_WhenNoScope()
    {
        var routeRedb = Substitute.For<IRedbService>();
        var context = Substitute.For<IRouteContext>();
        context.GetService<IRedbService>().Returns(routeRedb);

        var route = new RouteDefinition();
        route._context = context;

        IRedbService? observed = null;
        route.ProcessWithRedb(async (redb, ex, ct) =>
        {
            observed = redb;
            await Task.CompletedTask;
        });

        await InvokeAsync(route, context, new Exchange());
        observed.Should().BeSameAs(routeRedb);
    }

    [Fact]
    public async Task ProcessWithRedb_Async_EachExchange_GetsScopedService()
    {
        // Build a real DI container with a scoped service
        var services = new ServiceCollection();
        services.AddScoped<IRedbService>(_ => Substitute.For<IRedbService>());
        var rootProvider = services.BuildServiceProvider();
        var scopeFactory = rootProvider.GetRequiredService<IServiceScopeFactory>();

        Func<IExchange, CancellationToken, Task>? captured = null;
        var route = Substitute.For<IRouteDefinition>();
        route.Process(Arg.Any<Func<IExchange, CancellationToken, Task>>())
            .Returns(ci => { captured = ci.Arg<Func<IExchange, CancellationToken, Task>>(); return route; });

        IRedbService? first = null;
        IRedbService? second = null;

        route.ProcessWithRedb(async (redb, ex, ct) =>
        {
            if (first == null) first = redb;
            else second = redb;
            await Task.CompletedTask;
        });

        captured.Should().NotBeNull();

        // Two exchanges with DIFFERENT scopes
        var scope1 = scopeFactory.CreateScope();
        var ex1 = Substitute.For<IExchange>();
        ex1.ServiceProvider.Returns(scope1.ServiceProvider);

        var scope2 = scopeFactory.CreateScope();
        var ex2 = Substitute.For<IExchange>();
        ex2.ServiceProvider.Returns(scope2.ServiceProvider);

        await captured!(ex1, CancellationToken.None);
        await captured!(ex2, CancellationToken.None);

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        first.Should().NotBeSameAs(second, "each exchange should get its own scoped IRedbService");

        scope1.Dispose();
        scope2.Dispose();
    }

    // ── Helper ───────────────────────────────────────────────────────

    private static IExchange CreateExchangeWithScope(IRedbService scopedRedb)
    {
        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(IRedbService)).Returns(scopedRedb);

        var exchange = Substitute.For<IExchange>();
        exchange.ServiceProvider.Returns(sp);

        return exchange;
    }

    // ── Named ProcessWithRedb (async) ───────────────────────────────

    [Fact]
    public async Task ProcessWithRedb_Named_Async_ResolvesFromRegistry()
    {
        var namedRedb = Substitute.For<IRedbService>();
        var context = Substitute.For<IRouteContext>();
        context.GetFromRegistry<IServiceScopeFactory>(Arg.Any<string>()).Returns((IServiceScopeFactory?)null);
        context.GetFromRegistry<IRedbService>("redb:orders-db").Returns(namedRedb);

        var route = new RouteDefinition();
        route._context = context;

        IRedbService? observed = null;
        route.ProcessWithRedb("orders-db", async (redb, ex, ct) =>
        {
            observed = redb;
            await Task.CompletedTask;
        });

        await InvokeAsync(route, context, new Exchange());
        observed.Should().BeSameAs(namedRedb);
    }

    // ── Named ProcessWithRedb (sync) ────────────────────────────────

    [Fact]
    public async Task ProcessWithRedb_Named_Sync_ResolvesFromRegistry()
    {
        var namedRedb = Substitute.For<IRedbService>();
        var context = Substitute.For<IRouteContext>();
        context.GetFromRegistry<IServiceScopeFactory>(Arg.Any<string>()).Returns((IServiceScopeFactory?)null);
        context.GetFromRegistry<IRedbService>("redb:orders-db").Returns(namedRedb);

        var route = new RouteDefinition();
        route._context = context;

        IRedbService? observed = null;
        route.ProcessWithRedb("orders-db", (redb, ex) => { observed = redb; });

        await InvokeAsync(route, context, new Exchange());
        observed.Should().BeSameAs(namedRedb);
    }

    // ── Named SetBodyFromRedb ───────────────────────────────────────

    [Fact]
    public async Task SetBodyFromRedb_Named_ResolvesFromRegistry()
    {
        var namedRedb = Substitute.For<IRedbService>();
        var context = Substitute.For<IRouteContext>();
        context.GetFromRegistry<IServiceScopeFactory>(Arg.Any<string>()).Returns((IServiceScopeFactory?)null);
        context.GetFromRegistry<IRedbService>("redb:analytics").Returns(namedRedb);

        var route = new RouteDefinition();
        route._context = context;

        IRedbService? observed = null;
        route.SetBodyFromRedb("analytics", (redb, ex) => { observed = redb; return "data"; });

        var exchange = new Exchange();
        await InvokeAsync(route, context, exchange);
        observed.Should().BeSameAs(namedRedb);
        exchange.In.Body.Should().Be("data");
    }

    // ── Named SetHeaderFromRedb ─────────────────────────────────────

    [Fact]
    public async Task SetHeaderFromRedb_Named_ResolvesFromRegistry()
    {
        var namedRedb = Substitute.For<IRedbService>();
        var context = Substitute.For<IRouteContext>();
        context.GetFromRegistry<IServiceScopeFactory>(Arg.Any<string>()).Returns((IServiceScopeFactory?)null);
        context.GetFromRegistry<IRedbService>("redb:orders-db").Returns(namedRedb);

        var route = new RouteDefinition();
        route._context = context;

        IRedbService? observed = null;
        route.SetHeaderFromRedb("orders-db", "X-Count", (redb, ex) => { observed = redb; return 99; });

        var exchange = new Exchange();
        await InvokeAsync(route, context, exchange);
        observed.Should().BeSameAs(namedRedb);
        exchange.In.Headers["X-Count"].Should().Be(99);
    }

    // ── Named: empty name falls back to default ─────────────────────

    [Fact]
    public async Task ProcessWithRedb_Named_EmptyName_FallsBackToDefault()
    {
        var defaultRedb = Substitute.For<IRedbService>();
        var context = Substitute.For<IRouteContext>();
        context.GetService<IRedbService>().Returns(defaultRedb);

        var route = new RouteDefinition();
        route._context = context;

        IRedbService? observed = null;
        route.ProcessWithRedb("", async (redb, ex, ct) =>
        {
            observed = redb;
            await Task.CompletedTask;
        });

        await InvokeAsync(route, context, new Exchange());
        observed.Should().BeSameAs(defaultRedb);
    }

    // ── Named: not found throws ─────────────────────────────────────

    [Fact]
    public async Task ProcessWithRedb_Named_NotFound_Throws()
    {
        var context = Substitute.For<IRouteContext>();
        context.GetFromRegistry<IServiceScopeFactory>(Arg.Any<string>()).Returns((IServiceScopeFactory?)null);
        context.GetFromRegistry<IRedbService>("redb:missing").Returns((IRedbService?)null);

        var route = new RouteDefinition();
        route._context = context;

        route.ProcessWithRedb("missing", (redb, ex) => { });

        var act = async () => await InvokeAsync(route, context, new Exchange());
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Named IRedbService 'missing'*not found*");
    }

    // ── Named + IServiceScopeFactory: per-exchange scoping ───────────

    /// <summary>
    /// When a named <see cref="IServiceScopeFactory"/> is registered (as TsakContextManager
    /// does under <c>"redb-factory:{name}"</c>), each exchange gets its own scoped
    /// <see cref="IRedbService"/>; repeat calls within the same exchange return the cached scope.
    /// </summary>
    [Fact]
    public async Task ProcessWithRedb_Named_WithFactory_PerExchangeScope_AndCacheReuse()
    {
        // Real DI container with scoped IRedbService.
        var services = new ServiceCollection();
        services.AddScoped<IRedbService>(_ => Substitute.For<IRedbService>());
        var rootProvider = services.BuildServiceProvider();
        var scopeFactory = rootProvider.GetRequiredService<IServiceScopeFactory>();

        // Mocked context: factory present at "redb-factory:orders-db".
        var context = Substitute.For<IRouteContext>();
        context.GetFromRegistry<IServiceScopeFactory>("redb-factory:orders-db").Returns(scopeFactory);

        var route = new RouteDefinition();
        route._context = context;

        var resolved = new List<IRedbService>();
        route.ProcessWithRedb("orders-db", async (redb, ex, ct) =>
        {
            resolved.Add(redb);
            await Task.CompletedTask;
        });

        var processor = route.Outputs[0].CreateProcessor(context);

        var ex1 = new Exchange();
        var ex2 = new Exchange();

        // First exchange — invoke twice to exercise cache reuse.
        await processor.Process(ex1, CancellationToken.None);
        await processor.Process(ex1, CancellationToken.None);
        // Second exchange — must get its own scoped instance.
        await processor.Process(ex2, CancellationToken.None);

        resolved.Should().HaveCount(3);
        resolved[0].Should().BeSameAs(resolved[1], "second call within same exchange must reuse the cached scope");
        resolved[0].Should().NotBeSameAs(resolved[2], "different exchanges must get different scoped IRedbService");
    }

    // ── Helper to invoke a real RouteDefinition's first output processor ─────

    private static async Task InvokeAsync(RouteDefinition route, IRouteContext context, IExchange exchange)
    {
        var processor = route.Outputs[0].CreateProcessor(context);
        await processor.Process(exchange, CancellationToken.None);
    }
}
