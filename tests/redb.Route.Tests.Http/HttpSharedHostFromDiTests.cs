using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;
using redb.Route.Processors;

namespace redb.Route.Tests.Http;

/// <summary>
/// Where an <c>http:</c> consumer looks for the shared Kestrel host: explicitly assigned, then the
/// context's DI container, then the refusal. The middle step is what a module host needs — it adds
/// components by scanning, so <c>ServerManager</c> is never assigned, while the one shared manager
/// sits in DI. Requested by the Tsak agent 2026-09-24, whose module routes failed at start with
/// "SharedHttpServerManager is not configured" although the manager was right there in the container;
/// gRPC and AS2 already resolved in this order.
/// </summary>
public class HttpSharedHostFromDiTests
{
    private static IServiceProvider ProviderWith(SharedHttpServerManager manager)
        => new ServiceCollection().AddSingleton(manager).BuildServiceProvider();

    private static IConsumer CreateConsumer(RouteContext context, HttpComponent component)
    {
        context.AddComponent(component);
        var endpoint = context.GetEndpoint("http://localhost:18321/orders");
        return endpoint.CreateConsumer(new DelegateProcessor(_ => { }));
    }

    [Fact]
    public void The_manager_in_the_contexts_container_serves_the_consumer()
    {
        var shared = new SharedHttpServerManager();
        using var context = new RouteContext();
        context.SetServiceProvider(ProviderWith(shared));

        var act = () => CreateConsumer(context, new HttpComponent());

        act.Should().NotThrow("the shared host is in the container the context was given");
    }

    [Fact]
    public void An_assigned_manager_wins_over_the_container()
    {
        var assigned = new SharedHttpServerManager();
        using var context = new RouteContext();
        context.SetServiceProvider(ProviderWith(new SharedHttpServerManager()));

        var component = new HttpComponent { ServerManager = assigned };
        var act = () => CreateConsumer(context, component);

        act.Should().NotThrow();
        component.ServerManager.Should().BeSameAs(assigned);
    }

    [Fact]
    public void A_manager_set_as_a_context_service_serves_the_consumer_too()
    {
        var shared = new SharedHttpServerManager();
        using var context = new RouteContext();
        context.AddService(typeof(SharedHttpServerManager), shared);

        var act = () => CreateConsumer(context, new HttpComponent());

        // Both places a host can leave it are read in one order, the context services first:
        // a host without a DI container has AddService and nothing else.
        act.Should().NotThrow();
    }

    [Fact]
    public void With_neither_the_refusal_still_names_the_registration_call()
    {
        using var context = new RouteContext();

        var act = () => CreateConsumer(context, new HttpComponent());

        // No silent private listener here: an http: consumer binds a port, and which port it binds
        // is the host's decision, so the missing host stays a loud failure.
        act.Should().Throw<InvalidOperationException>().WithMessage("*AddRedbRouteHttp*");
    }
}
