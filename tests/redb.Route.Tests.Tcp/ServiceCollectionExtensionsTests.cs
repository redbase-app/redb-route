using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Tcp;

namespace redb.Route.Tests.Tcp;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddRedbRouteTcp_RegistersTcpComponent()
    {
        var services = new ServiceCollection();
        services.AddRedbRouteTcp();
        var sp = services.BuildServiceProvider();

        var component = sp.GetService<TcpComponent>();
        component.Should().NotBeNull();
        component!.Scheme.Should().Be("tcp");
    }

    [Fact]
    public void AddRedbRouteTcp_ComponentIsSingleton()
    {
        var services = new ServiceCollection();
        services.AddRedbRouteTcp();
        var sp = services.BuildServiceProvider();

        var first = sp.GetService<TcpComponent>();
        var second = sp.GetService<TcpComponent>();
        first.Should().BeSameAs(second);
    }

    [Fact]
    public void AddRedbRouteTcp_RegistersWithRouteContext()
    {
        var services = new ServiceCollection();
        var context = Substitute.For<IRouteContext>();
        services.AddSingleton(context);
        services.AddRedbRouteTcp();
        var sp = services.BuildServiceProvider();

        // Force ITcpComponentRegistrar resolution to trigger AddComponent
        sp.GetService<ITcpComponentRegistrar>();

        context.Received(1).AddComponent(Arg.Is<TcpComponent>(c => c.Scheme == "tcp"));
    }

    [Fact]
    public void AddRedbRouteTcp_ReturnsServiceCollection()
    {
        var services = new ServiceCollection();
        var result = services.AddRedbRouteTcp();
        result.Should().BeSameAs(services);
    }
}
