using Microsoft.Extensions.DependencyInjection;
using redb.Route.Core;
using redb.Route.Http;
using redb.Route.Soap;

namespace redb.Route.Tests.Soap;

/// <summary>
/// Where the component looks for the shared Kestrel host: explicitly assigned, then the context's DI
/// container, then its own. The middle step is what a module host needs — it adds components by
/// scanning (so <c>ServerManager</c> is never assigned) while the one shared manager sits in DI.
/// Requested by the Tsak agent 2026-09-24; gRPC and AS2 already resolved in this order.
/// </summary>
public class SoapComponentSharedHostFromDiTests
{
    private static IServiceProvider ProviderWith(SharedHttpServerManager manager)
        => new ServiceCollection().AddSingleton(manager).BuildServiceProvider();

    [Fact]
    public void The_manager_in_the_contexts_container_is_used_when_none_was_assigned()
    {
        var shared = new SharedHttpServerManager();
        var component = new SoapComponent();

        using var context = new RouteContext();
        context.SetServiceProvider(ProviderWith(shared));
        context.AddComponent(component);

        component.Server.Should().BeSameAs(shared);
    }

    [Fact]
    public void An_assigned_manager_wins_over_the_container()
    {
        var assigned = new SharedHttpServerManager();
        var component = new SoapComponent { ServerManager = assigned };

        using var context = new RouteContext();
        context.SetServiceProvider(ProviderWith(new SharedHttpServerManager()));
        context.AddComponent(component);

        component.Server.Should().BeSameAs(assigned);
    }

    [Fact]
    public void A_manager_set_as_a_context_service_is_found_too()
    {
        var shared = new SharedHttpServerManager();
        var component = new SoapComponent();

        using var context = new RouteContext();
        context.AddService(typeof(SharedHttpServerManager), shared);
        context.AddComponent(component);

        // Both places a host can leave it are read in one order, the context services first:
        // a host without a DI container has AddService and nothing else.
        component.Server.Should().BeSameAs(shared);
    }

    [Fact]
    public void Without_either_the_component_still_owns_one()
    {
        var component = new SoapComponent();

        using var context = new RouteContext();
        context.AddComponent(component);

        component.Server.Should().NotBeNull();
    }
}
