using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Extensions;

namespace redb.Route.Tests.Extensions;

/// <summary>
/// Tests for DI registration via <see cref="ServiceCollectionExtensions.AddRedbRoute"/>.
/// </summary>
public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddRedbRoute_RegistersEngine()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddRedbRoute();

        var sp = services.BuildServiceProvider();
        var ctx = sp.GetService<RouteContext>();

        ctx.Should().NotBeNull();
    }

    [Fact]
    public void AddRedbRoute_RegistersIRouteContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddRedbRoute();

        var sp = services.BuildServiceProvider();
        var ctx = sp.GetService<IRouteContext>();

        ctx.Should().NotBeNull();
    }

    [Fact]
    public void AddRedbRoute_EngineSingleton_ReturnsSameInstance()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddRedbRoute();

        var sp = services.BuildServiceProvider();
        var e1 = sp.GetRequiredService<RouteContext>();
        var e2 = sp.GetRequiredService<RouteContext>();

        e1.Should().BeSameAs(e2);
    }

    [Fact]
    public void AddRedbRoute_WithInlineRoutes_RegistersConfigurator()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddRedbRoute(route =>
        {
            route.AddRoutes(r =>
            {
                r.From("direct://di-test")
                    .Process(_ => { });
            });
        });

        var sp = services.BuildServiceProvider();
        var configurators = sp.GetServices<IRouteContextConfigurator>();

        configurators.Should().NotBeEmpty();
    }

    [Fact]
    public void AddRedbRouteCheck_RegistersHealthCheck()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRedbRoute();
        services.AddHealthChecks()
            .AddRedbRouteCheck();

        var sp = services.BuildServiceProvider();
        var healthCheckProvider = sp.GetService<HealthCheckService>();

        healthCheckProvider.Should().NotBeNull();
    }
}
