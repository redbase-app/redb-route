using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// Tests for Exchange DI scope lifecycle: creation, propagation, disposal.
/// </summary>
public class ExchangeScopeTests
{
    // ── Factory tests ──

    [Fact]
    public void Create_WithoutScopeFactory_HasNullServiceProvider()
    {
        var exchange = Exchange.Create(new Message("test"), null);

        exchange.ServiceProvider.Should().BeNull();
        exchange.In.Body.Should().Be("test");
    }

    [Fact]
    public void Create_WithScopeFactory_HasServiceProvider()
    {
        var services = new ServiceCollection()
            .AddSingleton("hello")
            .BuildServiceProvider();
        var factory = services.GetRequiredService<IServiceScopeFactory>();

        var exchange = Exchange.Create(new Message("test"), factory);

        exchange.ServiceProvider.Should().NotBeNull();
        exchange.ServiceProvider!.GetService<string>().Should().Be("hello");
    }

    [Fact]
    public void Create_EachCall_CreatesIsolatedScope()
    {
        var services = new ServiceCollection()
            .AddScoped<Marker>()
            .BuildServiceProvider();
        var factory = services.GetRequiredService<IServiceScopeFactory>();

        var ex1 = Exchange.Create(new Message(), factory);
        var ex2 = Exchange.Create(new Message(), factory);

        var m1 = ex1.ServiceProvider!.GetRequiredService<Marker>();
        var m2 = ex2.ServiceProvider!.GetRequiredService<Marker>();

        m1.Should().NotBeSameAs(m2);
    }

    // ── Clone tests ──

    [Fact]
    public void Clone_WithScopeFactory_CreatesNewScope()
    {
        var services = new ServiceCollection()
            .AddScoped<Marker>()
            .BuildServiceProvider();
        var factory = services.GetRequiredService<IServiceScopeFactory>();

        var original = Exchange.Create(new Message("data"), factory);
        var clone = original.Clone();

        clone.ServiceProvider.Should().NotBeNull();
        clone.In.Body.Should().Be("data");

        var originalMarker = original.ServiceProvider!.GetRequiredService<Marker>();
        var cloneMarker = clone.ServiceProvider!.GetRequiredService<Marker>();
        cloneMarker.Should().NotBeSameAs(originalMarker);
    }

    [Fact]
    public void Clone_WithoutScopeFactory_HasNullServiceProvider()
    {
        var original = new Exchange(new Message("data"));
        var clone = original.Clone();

        clone.ServiceProvider.Should().BeNull();
    }

    // ── CreateChild tests ──

    [Fact]
    public void CreateChild_CreatesNewScopeWithDifferentMessage()
    {
        var services = new ServiceCollection()
            .AddScoped<Marker>()
            .BuildServiceProvider();
        var factory = services.GetRequiredService<IServiceScopeFactory>();

        var parent = Exchange.Create(new Message("parent"), factory);
        parent.RouteId = "route-1";
        parent.Properties["key"] = "value";

        var child = parent.CreateChild(new Message("child"));

        child.In.Body.Should().Be("child");
        child.RouteId.Should().Be("route-1");
        child.Properties["key"].Should().Be("value");
        child.ServiceProvider.Should().NotBeNull();

        var parentMarker = parent.ServiceProvider!.GetRequiredService<Marker>();
        var childMarker = child.ServiceProvider!.GetRequiredService<Marker>();
        childMarker.Should().NotBeSameAs(parentMarker);
    }

    // ── Dispose tests ──

    [Fact]
    public async Task DisposeAsync_DisposesScope()
    {
        var services = new ServiceCollection()
            .AddScoped<Marker>()
            .BuildServiceProvider();
        var factory = services.GetRequiredService<IServiceScopeFactory>();

        var exchange = Exchange.Create(new Message(), factory);
        exchange.ServiceProvider.Should().NotBeNull();

        await exchange.DisposeAsync();

        exchange.ServiceProvider.Should().BeNull();
    }

    [Fact]
    public async Task DisposeAsync_WithoutScope_DoesNotThrow()
    {
        var exchange = new Exchange(new Message());

        await exchange.DisposeAsync(); // Should not throw
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_DoesNotThrow()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var factory = services.GetRequiredService<IServiceScopeFactory>();

        var exchange = Exchange.Create(new Message(), factory);

        await exchange.DisposeAsync();
        await exchange.DisposeAsync(); // Second call should be safe
    }

    [Fact]
    public async Task DisposeAsync_Clone_DoesNotAffectOriginal()
    {
        var services = new ServiceCollection()
            .AddScoped<Marker>()
            .BuildServiceProvider();
        var factory = services.GetRequiredService<IServiceScopeFactory>();

        var original = Exchange.Create(new Message(), factory);
        var clone = original.Clone();

        await clone.DisposeAsync();

        clone.ServiceProvider.Should().BeNull();
        original.ServiceProvider.Should().NotBeNull();
    }

    [Fact]
    public async Task DisposeAsync_DisposesRegisteredDisposables()
    {
        var services = new ServiceCollection()
            .AddScoped<DisposableService>()
            .BuildServiceProvider();
        var factory = services.GetRequiredService<IServiceScopeFactory>();

        var exchange = Exchange.Create(new Message(), factory);
        var svc = exchange.ServiceProvider!.GetRequiredService<DisposableService>();
        svc.IsDisposed.Should().BeFalse();

        await exchange.DisposeAsync();

        svc.IsDisposed.Should().BeTrue();
    }

    // ── IExchange default interface tests ──

    [Fact]
    public void IExchange_ServiceProvider_DefaultsToNull()
    {
        IExchange exchange = Substitute.For<IExchange>();
        // Default interface member returns null
        // NSubstitute won't call the default impl, so we verify via concrete
        var concreteExchange = new Exchange();
        ((IExchange)concreteExchange).ServiceProvider.Should().BeNull();
    }

    [Fact]
    public async Task IExchange_DisposeAsync_DefaultIsNoop()
    {
        // The default IAsyncDisposable.DisposeAsync on IExchange returns default ValueTask
        IAsyncDisposable exchange = new Exchange();
        await exchange.DisposeAsync(); // Should not throw
    }

    // ── Helpers ──

    private class Marker { }

    private class DisposableService : IDisposable
    {
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }
}
