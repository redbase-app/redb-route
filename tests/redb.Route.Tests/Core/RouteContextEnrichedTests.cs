using FluentAssertions;
using NSubstitute;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// Tests for the enriched <see cref="RouteContext"/>:
/// properties, registry, service locator, component management,
/// three-tier exception handling, degraded-mode start, and dispose.
/// </summary>
public class RouteContextEnrichedTests : IDisposable
{
    private readonly RouteContext _ctx = new("test-ctx-001");

    public void Dispose() => _ctx.Dispose();

    // ── Identity ──

    [Fact]
    public void ContextId_ReturnsProvidedId()
    {
        _ctx.ContextId.Should().Be("test-ctx-001");
    }

    [Fact]
    public void ContextId_GeneratesGuid_WhenNull()
    {
        using var ctx = new RouteContext();
        ctx.ContextId.Should().NotBeNullOrEmpty();
        ctx.ContextId.Should().HaveLength(32); // GUID "N" format
    }

    [Fact]
    public void IsStarted_IsFalseByDefault()
    {
        _ctx.IsStarted.Should().BeFalse();
    }

    // ── Properties ──

    [Fact]
    public void Indexer_GetSet_Works()
    {
        _ctx["key1"] = "value1";
        _ctx["key1"].Should().Be("value1");
    }

    [Fact]
    public void Indexer_MissingKey_ReturnsNull()
    {
        _ctx["nonexistent"].Should().BeNull();
    }

    [Fact]
    public void GetProperty_TypedConversion()
    {
        _ctx["count"] = 42;
        _ctx.GetProperty<int>("count").Should().Be(42);
    }

    [Fact]
    public void GetProperty_MissingKey_ReturnsDefault()
    {
        _ctx.GetProperty<int>("missing").Should().Be(0);
    }

    [Fact]
    public void SetProperty_ReturnsSelf_ForChaining()
    {
        var result = _ctx.SetProperty("x", 10);
        result.Should().BeSameAs(_ctx);
        _ctx.GetProperty<int>("x").Should().Be(10);
    }

    // ── Registry ──

    [Fact]
    public void AddToRegistry_GetFromRegistry_Works()
    {
        var factory = new object();
        _ctx.AddToRegistry("myFactory", factory);
        _ctx.GetFromRegistry<object>("myFactory").Should().BeSameAs(factory);
    }

    [Fact]
    public void GetFromRegistry_MissingKey_ReturnsDefault()
    {
        _ctx.GetFromRegistry<string>("missing").Should().BeNull();
    }

    [Fact]
    public void AddToRegistry_ReturnsSelf_ForChaining()
    {
        _ctx.AddToRegistry("a", "b").Should().BeSameAs(_ctx);
    }

    [Fact]
    public void AddToRegistry_ThrowsOnNullKey()
    {
        var act = () => _ctx.AddToRegistry("", "val");
        act.Should().Throw<ArgumentException>();
    }

    // ── Service Locator ──

    [Fact]
    public void AddService_GetService_Works()
    {
        var svc = Substitute.For<IDisposable>();
        _ctx.AddService(typeof(IDisposable), svc);
        _ctx.GetService<IDisposable>().Should().BeSameAs(svc);
    }

    [Fact]
    public void GetService_NotRegistered_ReturnsDefault()
    {
        _ctx.GetService<IDisposable>().Should().BeNull();
    }

    // ── Component Management ──

    [Fact]
    public void HasComponent_ReturnsFalse_WhenNotRegistered()
    {
        _ctx.HasComponent("kafka").Should().BeFalse();
    }

    [Fact]
    public void HasComponent_ReturnsTrue_AfterRegistration()
    {
        var component = CreateMockComponent("test");
        _ctx.AddComponent(component);
        _ctx.HasComponent("test").Should().BeTrue();
    }

    [Fact]
    public void GetComponentNames_ReturnsRegisteredSchemes()
    {
        _ctx.AddComponent(CreateMockComponent("alpha"));
        _ctx.AddComponent(CreateMockComponent("beta"));
        _ctx.GetComponentNames().Should().Contain("alpha").And.Contain("beta");
    }

    [Fact]
    public void GetComponent_ByScheme_ReturnsCorrectComponent()
    {
        var comp = CreateMockComponent("direct");
        _ctx.AddComponent(comp);
        _ctx.GetComponent<IComponent>("direct").Should().BeSameAs(comp);
    }

    [Fact]
    public void GetComponent_ByType_ReturnsFirstMatch()
    {
        var comp = CreateMockComponent("custom-typed");
        _ctx.AddComponent(comp);
        _ctx.GetComponent<IComponent>("custom-typed").Should().BeSameAs(comp);
    }

    [Fact]
    public void GetComponent_NotFound_ReturnsNull()
    {
        _ctx.GetComponent<IComponent>("missing").Should().BeNull();
    }

    // ── Exception Handling ──

    [Fact]
    public async Task HandleException_GlobalHandler_IsCalled()
    {
        var processor = Substitute.For<IProcessor>();
        _ctx.AddGlobalExceptionHandler<InvalidOperationException>(processor);

        var exchange = new Exchange();
        await _ctx.HandleException(exchange, new InvalidOperationException("test"));

        await processor.Received(1).Process(Arg.Is<IExchange>(ex => ex.Exception is InvalidOperationException));
    }

    [Fact]
    public async Task HandleException_LocalHandler_HasPriorityOverGlobal()
    {
        var globalProcessor = Substitute.For<IProcessor>();
        var localProcessor = Substitute.For<IProcessor>();

        _ctx.AddGlobalExceptionHandler<InvalidOperationException>(globalProcessor);
        _ctx.AddLocalExceptionHandler("route1", typeof(InvalidOperationException), localProcessor);

        var exchange = new Exchange { RouteId = "route1" };
        await _ctx.HandleException(exchange, new InvalidOperationException("test"));

        await localProcessor.Received(1).Process(Arg.Any<IExchange>());
        await globalProcessor.DidNotReceive().Process(Arg.Any<IExchange>());
    }

    [Fact]
    public async Task HandleException_WalksTypeHierarchy()
    {
        var processor = Substitute.For<IProcessor>();
        _ctx.AddGlobalExceptionHandler<Exception>(processor);

        var exchange = new Exchange();
        await _ctx.HandleException(exchange, new ArgumentNullException("param"));

        await processor.Received(1).Process(Arg.Any<IExchange>());
    }

    [Fact]
    public async Task HandleException_NoHandler_Throws()
    {
        var exchange = new Exchange();
        var act = () => _ctx.HandleException(exchange, new InvalidOperationException("unhandled"));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("No exception handler*");
    }

    [Fact]
    public async Task HandleException_SetsExceptionCaughtProperty()
    {
        var processor = Substitute.For<IProcessor>();
        _ctx.AddGlobalExceptionHandler<Exception>(processor);

        var exchange = new Exchange();
        var ex = new InvalidOperationException("caught");
        await _ctx.HandleException(exchange, ex);

        exchange.Properties["ExceptionCaught"].Should().BeSameAs(ex);
    }

    [Fact]
    public void AddGlobalExceptionHandler_DuplicateType_Throws()
    {
        var proc = Substitute.For<IProcessor>();
        _ctx.AddGlobalExceptionHandler<Exception>(proc);
        var act = () => _ctx.AddGlobalExceptionHandler<Exception>(proc);
        act.Should().Throw<InvalidOperationException>().WithMessage("*already registered*");
    }

    // ── Lifecycle ──

    [Fact]
    public async Task Start_SetsIsStarted()
    {
        await _ctx.Start();
        _ctx.IsStarted.Should().BeTrue();
    }

    [Fact]
    public async Task Stop_ClearsIsStarted()
    {
        await _ctx.Start();
        await _ctx.Stop();
        _ctx.IsStarted.Should().BeFalse();
    }

    [Fact]
    public void Dispose_ClearsAllStorage()
    {
        _ctx["key"] = "val";
        _ctx.AddToRegistry("r", "v");
        _ctx.AddService(typeof(IDisposable), Substitute.For<IDisposable>());
        _ctx.AddComponent(CreateMockComponent("test"));

        _ctx.Dispose();

        _ctx["key"].Should().BeNull();
        _ctx.GetFromRegistry<object>("r").Should().BeNull();
        _ctx.GetService<IDisposable>().Should().BeNull();
        _ctx.HasComponent("test").Should().BeFalse();
    }

    [Fact]
    public async Task DisposeAsync_ClearsAllStorage()
    {
        _ctx["key"] = "val";
        _ctx.AddToRegistry("r", "v");
        _ctx.AddService(typeof(IDisposable), Substitute.For<IDisposable>());
        _ctx.AddComponent(CreateMockComponent("test"));

        await ((IAsyncDisposable)_ctx).DisposeAsync();

        _ctx["key"].Should().BeNull();
        _ctx.GetFromRegistry<object>("r").Should().BeNull();
        _ctx.GetService<IDisposable>().Should().BeNull();
        _ctx.HasComponent("test").Should().BeFalse();
    }

    [Fact]
    public async Task DisposeAsync_StopsContextIfStarted()
    {
        await _ctx.Start();
        _ctx.IsStarted.Should().BeTrue();

        await ((IAsyncDisposable)_ctx).DisposeAsync();
        _ctx.IsStarted.Should().BeFalse();
    }

    [Fact]
    public void RouteContext_ImplementsIAsyncDisposable()
    {
        _ctx.Should().BeAssignableTo<IAsyncDisposable>();
    }

    // ── Helpers ──

    private static IComponent CreateMockComponent(string scheme)
    {
        var comp = Substitute.For<IComponent>();
        comp.Scheme.Returns(scheme);
        return comp;
    }

    // ── DI: ServiceProvider ──

    [Fact]
    public void GetServiceProvider_ReturnsNull_WhenNotSet()
    {
        _ctx.GetServiceProvider().Should().BeNull();
    }

    [Fact]
    public void SetServiceProvider_StoresAndReturnsProvider()
    {
        var sp = Substitute.For<IServiceProvider>();
        var result = _ctx.SetServiceProvider(sp);

        result.Should().BeSameAs(_ctx);
        _ctx.GetServiceProvider().Should().BeSameAs(sp);
    }

    [Fact]
    public void SetServiceProvider_ThrowsOnNull()
    {
        var act = () => _ctx.SetServiceProvider(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── ErrorHandler ──

    [Fact]
    public void ErrorHandler_IsNull_ByDefault()
    {
        _ctx.ErrorHandler.Should().BeNull();
    }

    [Fact]
    public async Task HandleException_UsesErrorHandler_WhenNoRouteHandlerMatches()
    {
        var handler = Substitute.For<IErrorHandler>();
        _ctx.ErrorHandler = handler;

        var exchange = Substitute.For<IExchange>();
        exchange.Properties.Returns(new Dictionary<string, object?>());

        var exception = new InvalidOperationException("boom");

        await _ctx.HandleException(exchange, exception);

        await handler.Received(1).HandleError(exchange, exception, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleException_PrefersLocalHandler_OverErrorHandler()
    {
        // Set up local handler
        var localProcessor = Substitute.For<IProcessor>();
        _ctx.AddLocalExceptionHandler("route1", typeof(InvalidOperationException), localProcessor);

        // Set up error handler
        var errorHandler = Substitute.For<IErrorHandler>();
        _ctx.ErrorHandler = errorHandler;

        var exchange = Substitute.For<IExchange>();
        exchange.RouteId.Returns("route1");
        exchange.Properties.Returns(new Dictionary<string, object?>());

        var exception = new InvalidOperationException("handled locally");
        await _ctx.HandleException(exchange, exception);

        await localProcessor.Received(1).Process(exchange, Arg.Any<CancellationToken>());
        await errorHandler.DidNotReceiveWithAnyArgs().HandleError(default!, default!, default);
    }

    [Fact]
    public async Task HandleException_PrefersGlobalHandler_OverErrorHandler()
    {
        var globalProcessor = Substitute.For<IProcessor>();
        _ctx.AddGlobalExceptionHandler<InvalidOperationException>(globalProcessor);

        var errorHandler = Substitute.For<IErrorHandler>();
        _ctx.ErrorHandler = errorHandler;

        var exchange = Substitute.For<IExchange>();
        exchange.Properties.Returns(new Dictionary<string, object?>());

        var exception = new InvalidOperationException("handled globally");
        await _ctx.HandleException(exchange, exception);

        await globalProcessor.Received(1).Process(exchange, Arg.Any<CancellationToken>());
        await errorHandler.DidNotReceiveWithAnyArgs().HandleError(default!, default!, default);
    }

    [Fact]
    public async Task HandleException_Throws_WhenNoHandlerAndNoErrorHandler()
    {
        var exchange = Substitute.For<IExchange>();
        exchange.Properties.Returns(new Dictionary<string, object?>());

        var exception = new InvalidOperationException("unhandled");

        var act = () => _ctx.HandleException(exchange, exception);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("No exception handler found*");
    }

    // ── HasExceptionRoute ──

    [Fact]
    public void HasExceptionRoute_ReturnsFalse_WhenNoHandlers()
    {
        _ctx.HasExceptionRoute<InvalidOperationException>().Should().BeFalse();
    }

    [Fact]
    public void HasExceptionRoute_ReturnsTrue_ForGlobalHandler()
    {
        var processor = Substitute.For<IProcessor>();
        _ctx.AddGlobalExceptionHandler<ArgumentException>(processor);

        _ctx.HasExceptionRoute<ArgumentException>().Should().BeTrue();
    }

    [Fact]
    public void HasExceptionRoute_ReturnsTrue_ForLocalHandler()
    {
        var processor = Substitute.For<IProcessor>();
        _ctx.AddLocalExceptionHandler("route1", typeof(TimeoutException), processor);

        _ctx.HasExceptionRoute<TimeoutException>().Should().BeTrue();
    }

    [Fact]
    public void HasExceptionRoute_ReturnsTrue_ForDerivedType_ViaBaseHandler()
    {
        var processor = Substitute.For<IProcessor>();
        _ctx.AddGlobalExceptionHandler<Exception>(processor);

        // ArgumentException derives from Exception
        _ctx.HasExceptionRoute<ArgumentException>().Should().BeTrue();
    }

    // ── RemoveComponent ──

    [Fact]
    public void RemoveComponent_ReturnsTrue_WhenRemoved()
    {
        var comp = CreateMockComponent("test-scheme");
        _ctx.AddComponent(comp);

        _ctx.RemoveComponent("test-scheme").Should().BeTrue();
        _ctx.HasComponent("test-scheme").Should().BeFalse();
    }

    [Fact]
    public void RemoveComponent_ReturnsFalse_WhenNotPresent()
    {
        _ctx.RemoveComponent("nonexistent").Should().BeFalse();
    }

    [Fact]
    public void RemoveComponent_ThrowsOnEmpty()
    {
        var act = () => _ctx.RemoveComponent("");
        act.Should().Throw<ArgumentException>();
    }
}
