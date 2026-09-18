using Microsoft.Extensions.DependencyInjection;
using redb.Core;
using redb.Route.Abstractions;
using redb.Route.RedbCore.Extensions;

namespace redb.Route.Tests.Core;

/// <summary>
/// <see cref="RedbRouteExtensions.CreateRedbScope"/>: a scope of its own per call for code without an exchange.
/// The published factory first, then a scope opened from the registered service, otherwise a refusal.
/// </summary>
public sealed class CreateRedbScopeTests
{
    private sealed class TrackingScope : IServiceScope, IAsyncDisposable
    {
        public TrackingScope(IServiceProvider provider) => ServiceProvider = provider;
        public int DisposeCount;
        public IServiceProvider ServiceProvider { get; }
        public void Dispose() => Interlocked.Increment(ref DisposeCount);
        public ValueTask DisposeAsync() { Interlocked.Increment(ref DisposeCount); return ValueTask.CompletedTask; }
    }

    private static IServiceScopeFactory ScopedRedbFactory() =>
        new ServiceCollection()
            .AddScoped<IRedbService>(_ => Substitute.For<IRedbService>())
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

    private static IRedbService ScopeSource()
    {
        var instance = Substitute.For<IRedbService>();
        instance.CanCreateScope.Returns(true);
        instance.CreateScope().Returns(_ => new RedbScope(Substitute.For<IRedbService>(), null));
        return instance;
    }

    // ── Named ────────────────────────────────────────────────────────

    [Fact]
    public async Task Named_PublishedFactory_Wins_AndEachCallGetsItsOwnService()
    {
        var instance = ScopeSource();
        var context = Substitute.For<IRouteContext>();
        context.GetFromRegistry<IServiceScopeFactory>("redb-factory:orders").Returns(ScopedRedbFactory());
        context.GetFromRegistry<IRedbService>("redb:orders").Returns(instance);

        await using var first = context.CreateRedbScope("orders");
        await using var second = context.CreateRedbScope("orders");

        first.Service.Should().NotBeSameAs(second.Service, "parallel callers must never share a connection");
        first.Service.Should().NotBeSameAs(instance);
        instance.DidNotReceive().CreateScope();
    }

    [Fact]
    public async Task Named_Factory_DisposingTheScope_DisposesTheContainerScope()
    {
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IRedbService)).Returns(Substitute.For<IRedbService>());
        var tracking = new TrackingScope(provider);
        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(tracking);
        var context = Substitute.For<IRouteContext>();
        context.GetFromRegistry<IServiceScopeFactory>("redb-factory:orders").Returns(factory);

        var scope = context.CreateRedbScope("#orders");
        tracking.DisposeCount.Should().Be(0);
        await scope.DisposeAsync();

        tracking.DisposeCount.Should().Be(1);
    }

    [Fact]
    public void Named_FactoryWithoutRedb_Throws_AndReleasesTheScope()
    {
        var tracking = new TrackingScope(Substitute.For<IServiceProvider>());
        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(tracking);
        var context = Substitute.For<IRouteContext>();
        context.GetFromRegistry<IServiceScopeFactory>("redb-factory:orders").Returns(factory);

        var act = () => context.CreateRedbScope("orders");

        act.Should().Throw<InvalidOperationException>().WithMessage("*does not provide IRedbService*");
        tracking.DisposeCount.Should().Be(1, "a failed resolution must not strand the scope it opened");
    }

    [Fact]
    public async Task Named_NoFactory_OpensScopeFromTheRegisteredInstance()
    {
        var instance = ScopeSource();
        var context = Substitute.For<IRouteContext>();
        context.GetFromRegistry<IServiceScopeFactory>(Arg.Any<string>()).Returns((IServiceScopeFactory?)null);
        context.GetFromRegistry<IRedbService>("redb:orders").Returns(instance);

        await using var first = context.CreateRedbScope("orders");
        await using var second = context.CreateRedbScope("orders");

        instance.Received(2).CreateScope();
        first.Service.Should().NotBeSameAs(second.Service);
        first.Service.Should().NotBeSameAs(instance);
    }

    [Fact]
    public void Named_InstanceBuiltWithoutContainer_Throws()
    {
        var instance = Substitute.For<IRedbService>();
        instance.CanCreateScope.Returns(false);
        var context = Substitute.For<IRouteContext>();
        context.GetFromRegistry<IServiceScopeFactory>(Arg.Any<string>()).Returns((IServiceScopeFactory?)null);
        context.GetFromRegistry<IRedbService>("redb:orders").Returns(instance);

        var act = () => context.CreateRedbScope("orders");

        act.Should().Throw<InvalidOperationException>().WithMessage("*orders*without a DI container*");
        instance.DidNotReceive().CreateScope();
    }

    [Fact]
    public void Named_NotRegistered_Throws()
    {
        var context = Substitute.For<IRouteContext>();
        context.GetFromRegistry<IServiceScopeFactory>(Arg.Any<string>()).Returns((IServiceScopeFactory?)null);
        context.GetFromRegistry<IRedbService>(Arg.Any<string>()).Returns((IRedbService?)null);

        var act = () => context.CreateRedbScope("orders");

        act.Should().Throw<InvalidOperationException>().WithMessage("*orders*is not found*");
    }

    // ── Default ──────────────────────────────────────────────────────

    [Fact]
    public async Task Default_ServiceRegisteredOnContext_OpensScopeFromIt()
    {
        var registered = ScopeSource();
        var context = Substitute.For<IRouteContext>();
        context.GetService<IRedbService>().Returns(registered);
        context.GetServiceProvider().Returns(new ServiceCollection()
            .AddScoped<IRedbService>(_ => Substitute.For<IRedbService>()).BuildServiceProvider());

        await using var scope = context.CreateRedbScope();

        registered.Received(1).CreateScope();
    }

    [Fact]
    public void Default_ServiceRegisteredOnContext_WithoutContainer_Throws()
    {
        var registered = Substitute.For<IRedbService>();
        registered.CanCreateScope.Returns(false);
        var context = Substitute.For<IRouteContext>();
        context.GetService<IRedbService>().Returns(registered);

        var act = () => context.CreateRedbScope(string.Empty);

        act.Should().Throw<InvalidOperationException>().WithMessage("*without a DI container*");
    }

    [Fact]
    public async Task Default_OnlyServiceProvider_EachCallGetsItsOwnScopedService()
    {
        var provider = new ServiceCollection()
            .AddScoped<IRedbService>(_ => Substitute.For<IRedbService>())
            .BuildServiceProvider();
        var context = Substitute.For<IRouteContext>();
        context.GetService<IRedbService>().Returns((IRedbService?)null);
        context.GetServiceProvider().Returns(provider);

        await using var first = context.CreateRedbScope();
        await using var second = context.CreateRedbScope();

        first.Service.Should().NotBeSameAs(second.Service);
    }

    [Fact]
    public void Default_NothingRegistered_Throws()
    {
        var context = Substitute.For<IRouteContext>();
        context.GetService<IRedbService>().Returns((IRedbService?)null);
        context.GetServiceProvider().Returns((IServiceProvider?)null);

        var act = () => context.CreateRedbScope();

        act.Should().Throw<InvalidOperationException>().WithMessage("*not registered*");
    }

    [Fact]
    public void NullContext_Throws()
    {
        var act = () => RedbRouteExtensions.CreateRedbScope(null!, "orders");

        act.Should().Throw<ArgumentNullException>().WithParameterName("context");
    }
}
