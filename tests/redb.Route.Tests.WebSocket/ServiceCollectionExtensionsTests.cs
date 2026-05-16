using Microsoft.Extensions.DependencyInjection;
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
    public void AddRedbRouteWebSocket_RegistersWithRouteContext()
    {
        var services = new ServiceCollection();
        var context = Substitute.For<IRouteContext>();
        services.AddSingleton(context);
        services.AddRedbRouteWebSocket();
        var sp = services.BuildServiceProvider();

        sp.GetService<IWsComponentRegistrar>();

        context.Received(1).AddComponent(Arg.Is<WsComponent>(c => c.Scheme == "ws"));
        context.Received(1).AddComponent(Arg.Is<WssComponent>(c => c.Scheme == "wss"));
    }

    [Fact]
    public void AddRedbRouteWebSocket_ReturnsServiceCollection()
    {
        var services = new ServiceCollection();
        var result = services.AddRedbRouteWebSocket();
        result.Should().BeSameAs(services);
    }
}
