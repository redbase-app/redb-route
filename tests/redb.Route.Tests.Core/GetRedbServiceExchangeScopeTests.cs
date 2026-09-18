using Microsoft.Extensions.DependencyInjection;
using redb.Core;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.RedbCore.Extensions;

namespace redb.Route.Tests.Core;

/// <summary>
/// A host outside Tsak registers its named database with <c>RegisterRedbService</c> and publishes no scope factory.
/// With an exchange, <see cref="RedbRouteExtensions.GetRedbService(IRouteContext, string, IExchange?)"/> handed every
/// exchange that one instance: one connection for all parallel exchanges. An instance built through a container now
/// opens a scope of the same database per exchange, as the published factory does in Tsak.
/// </summary>
public sealed class GetRedbServiceExchangeScopeTests
{
    private sealed class TrackingScope : IServiceScope, IAsyncDisposable
    {
        public int DisposeCount;
        public IServiceProvider ServiceProvider { get; } = Substitute.For<IServiceProvider>();
        public void Dispose() => Interlocked.Increment(ref DisposeCount);
        public ValueTask DisposeAsync() { Interlocked.Increment(ref DisposeCount); return ValueTask.CompletedTask; }
    }

    private static (IRedbService Instance, List<TrackingScope> Opened) ScopeSource()
    {
        var opened = new List<TrackingScope>();
        var instance = Substitute.For<IRedbService>();
        instance.CanCreateScope.Returns(true);
        instance.CreateScope().Returns(_ =>
        {
            var tracking = new TrackingScope();
            opened.Add(tracking);
            return new RedbScope(Substitute.For<IRedbService>(), tracking);
        });
        return (instance, opened);
    }

    private static IRouteContext NamedWithoutFactory(IRedbService instance)
    {
        var context = Substitute.For<IRouteContext>();
        context.GetFromRegistry<IServiceScopeFactory>(Arg.Any<string>()).Returns((IServiceScopeFactory?)null);
        context.GetFromRegistry<IRedbService>("redb:orders").Returns(instance);
        return context;
    }

    [Fact]
    public async Task Named_NoFactory_InstanceFromContainer_EachExchangeGetsItsOwnScope()
    {
        var (instance, opened) = ScopeSource();
        var context = NamedWithoutFactory(instance);
        var first = new Exchange();
        var second = new Exchange();

        var a1 = context.GetRedbService("orders", first);
        var a2 = context.GetRedbService("orders", first);
        var b = context.GetRedbService("orders", second);

        a1.Should().NotBeSameAs(instance, "parallel exchanges must not share the registered instance's connection");
        a2.Should().BeSameAs(a1, "one exchange keeps its scope for every step");
        b.Should().NotBeSameAs(a1);
        opened.Should().HaveCount(2);

        await first.DisposeAsync();
        opened[0].DisposeCount.Should().Be(1, "the scope ends with its exchange");
        opened[1].DisposeCount.Should().Be(0);
        await second.DisposeAsync();
        opened[1].DisposeCount.Should().Be(1);
    }

    [Fact]
    public void Named_NoFactory_InstanceWithoutContainer_KeepsTheSharedInstance()
    {
        var instance = Substitute.For<IRedbService>();
        instance.CanCreateScope.Returns(false);
        var context = NamedWithoutFactory(instance);

        context.GetRedbService("orders", new Exchange()).Should().BeSameAs(instance);
        instance.DidNotReceive().CreateScope();
    }

    [Fact]
    public void Named_PublishedFactory_Wins_InstanceIsNotAsked()
    {
        var (instance, opened) = ScopeSource();
        var context = Substitute.For<IRouteContext>();
        context.GetFromRegistry<IServiceScopeFactory>("redb-factory:orders").Returns(new ServiceCollection()
            .AddScoped<IRedbService>(_ => Substitute.For<IRedbService>())
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>());
        context.GetFromRegistry<IRedbService>("redb:orders").Returns(instance);

        context.GetRedbService("orders", new Exchange()).Should().NotBeSameAs(instance);
        opened.Should().BeEmpty();
    }

    [Fact]
    public void Named_WithoutExchange_KeepsTheInstance_ForStartUpCode()
    {
        var (instance, opened) = ScopeSource();
        var context = NamedWithoutFactory(instance);

        context.GetRedbService("orders", exchange: null).Should().BeSameAs(instance);
        opened.Should().BeEmpty();
    }

    [Fact]
    public async Task Default_ServiceRegisteredOnContext_NoProvider_EachExchangeGetsItsOwnScope()
    {
        var (registered, opened) = ScopeSource();
        var context = Substitute.For<IRouteContext>();
        context.GetService<IRedbService>().Returns(registered);
        context.GetServiceProvider().Returns((IServiceProvider?)null);
        var first = new Exchange();
        var second = new Exchange();

        var a1 = context.GetRedbService(string.Empty, first);
        var a2 = context.GetRedbService(string.Empty, first);
        var b = context.GetRedbService(string.Empty, second);

        a1.Should().NotBeSameAs(registered);
        a2.Should().BeSameAs(a1);
        b.Should().NotBeSameAs(a1);

        await first.DisposeAsync();
        opened[0].DisposeCount.Should().Be(1);
    }
}
