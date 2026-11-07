using Microsoft.Extensions.DependencyInjection;
using redb.Route.Core;
using redb.Route.Extensions;
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
    public async Task AddRedbRouteTcp_RegistersWithRouteContext()
    {
        var services = new ServiceCollection();
        services.AddRedbRouteTcp();
        await using var sp = services.BuildServiceProvider();
        await using var context = new RouteContext();

        // The hook RouteHostedService applies at startup
        foreach (var configurator in sp.GetServices<IRouteContextConfigurator>())
            configurator.Configure(context);

        context.HasComponent("tcp").Should().BeTrue();
    }

    [Fact]
    public void AddRedbRouteTcp_ReturnsServiceCollection()
    {
        var services = new ServiceCollection();
        var result = services.AddRedbRouteTcp();
        result.Should().BeSameAs(services);
    }
}
