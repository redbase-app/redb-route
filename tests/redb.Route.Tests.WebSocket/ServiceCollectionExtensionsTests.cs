using Microsoft.Extensions.DependencyInjection;
using redb.Route.Core;
using redb.Route.Extensions;
using redb.Route.Abstractions;
using redb.Route.WebSocket;

namespace redb.Route.Tests.WebSocket;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddRedbRouteWebSocket_RegistersWsComponent()
    {
        var services = new ServiceCollection();
        services.AddRedbRouteWebSocket();
        var sp = services.BuildServiceProvider();

        var component = sp.GetService<WsComponent>();
        component.Should().NotBeNull();
        component!.Scheme.Should().Be("ws");
    }

    [Fact]
    public void AddRedbRouteWebSocket_RegistersWssComponent()
    {
        var services = new ServiceCollection();
        services.AddRedbRouteWebSocket();
        var sp = services.BuildServiceProvider();

        var component = sp.GetService<WssComponent>();
        component.Should().NotBeNull();
        component!.Scheme.Should().Be("wss");
    }

    [Fact]
    public void AddRedbRouteWebSocket_ComponentsAreSingleton()
    {
        var services = new ServiceCollection();
        services.AddRedbRouteWebSocket();
        var sp = services.BuildServiceProvider();

        var first = sp.GetService<WsComponent>();
        var second = sp.GetService<WsComponent>();
        first.Should().BeSameAs(second);
    }

    [Fact]
    public async Task AddRedbRouteWebSocket_RegistersWithRouteContext()
    {
        var services = new ServiceCollection();
        services.AddRedbRouteWebSocket();
        await using var sp = services.BuildServiceProvider();
        await using var context = new RouteContext();

        foreach (var configurator in sp.GetServices<IRouteContextConfigurator>())
            configurator.Configure(context);

        context.HasComponent("ws").Should().BeTrue();
        context.HasComponent("wss").Should().BeTrue();
    }

    [Fact]
    public void AddRedbRouteWebSocket_ReturnsServiceCollection()
    {
        var services = new ServiceCollection();
        var result = services.AddRedbRouteWebSocket();
        result.Should().BeSameAs(services);
    }
}
