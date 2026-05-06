using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;

namespace redb.Route.Tests.Core;

/// <summary>
/// Tests that IRouteContext is properly propagated from RouteBuilder → RouteDefinition → scopes → extensions.
/// </summary>
public class RouteContextPropagationTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.Stop();
    }

    // ── Basic propagation ──

    [Fact]
    public void GetContext_ReturnsNull_BeforeEngineStart()
    {
        var rd = new RouteDefinition();
        rd.GetContext().Should().BeNull();
    }

    // ── Propagation through RouteBuilder → RouteDefinition ──

    [Fact]
    public async Task Context_IsAvailable_InRouteDefinition_AfterStart()
    {
        IRouteContext? captured = null;

        _context.AddRoutes(r =>
        {
            var rd = r.From("direct://ctx-test-in");
            captured = rd.GetContext();
            rd.To("direct://ctx-test-out");
        });

        await _context.Start();

        captured.Should().NotBeNull();
        captured.Should().BeSameAs(_context);
    }

    // ── Propagation into extension methods ──

    [Fact]
    public async Task Context_IsAvailable_InExtensionMethod()
    {
        IRouteContext? captured = null;

        _context.AddRoutes(r =>
        {
            r.From("direct://ext-test-in")
                .CaptureContext(ctx => captured = ctx)
                .To("direct://ext-test-out");
        });

        await _context.Start();

        captured.Should().NotBeNull();
        captured.Should().BeSameAs(_context);
    }

    // ── Propagation into scopes ──

    [Fact]
    public async Task Context_IsAvailable_InChoiceScope()
    {
        IRouteContext? captured = null;

        _context.AddRoutes(r =>
        {
            r.From("direct://choice-ctx-in")
                .Choice()
                    .When(e => true)
                        .CaptureContext(ctx => captured = ctx)
                .End()
                .To("direct://choice-ctx-out");
        });

        await _context.Start();

        captured.Should().NotBeNull();
        captured.Should().BeSameAs(_context);
    }

    [Fact]
    public async Task Context_IsAvailable_InLoopScope()
    {
        IRouteContext? captured = null;

        _context.AddRoutes(r =>
        {
            r.From("direct://loop-ctx-in")
                .Loop(1)
                    .CaptureContext(ctx => captured = ctx)
                .End()
                .To("direct://loop-ctx-out");
        });

        await _context.Start();

        captured.Should().NotBeNull();
        captured.Should().BeSameAs(_context);
    }

    [Fact]
    public async Task Context_IsAvailable_InTracedScope()
    {
        IRouteContext? captured = null;

        _context.AddRoutes(r =>
        {
            r.From("direct://traced-ctx-in")
                .Traced("test-span")
                    .CaptureContext(ctx => captured = ctx)
                .EndTraced()
                .To("direct://traced-ctx-out");
        });

        await _context.Start();

        captured.Should().NotBeNull();
        captured.Should().BeSameAs(_context);
    }

    [Fact]
    public async Task Context_IsAvailable_InLogScope()
    {
        IRouteContext? captured = null;

        _context.AddRoutes(r =>
        {
            r.From("direct://log-ctx-in")
                .Log(Microsoft.Extensions.Logging.LogLevel.Information)
                    .CaptureContext(ctx => captured = ctx)
                .End()
                .To("direct://log-ctx-out");
        });

        await _context.Start();

        captured.Should().NotBeNull();
        captured.Should().BeSameAs(_context);
    }

    [Fact]
    public async Task Context_IsAvailable_InDoTryScope()
    {
        IRouteContext? captured = null;

        _context.AddRoutes(r =>
        {
            r.From("direct://try-ctx-in")
                .DoTry()
                    .CaptureContext(ctx => captured = ctx)
                .DoCatch<Exception>()
                    .Process(e => { })
                .End()
                .To("direct://try-ctx-out");
        });

        await _context.Start();

        captured.Should().NotBeNull();
        captured.Should().BeSameAs(_context);
    }

    // ── Extension method can use context for DI ──

    [Fact]
    public async Task ExtensionMethod_CanAccessServices_ViaContext()
    {
        var processed = false;

        _context.AddToRegistry("testFlag", true);

        _context.AddRoutes(r =>
        {
            r.From("direct://svc-test-in")
                .UseRegistryFlag("testFlag", flag => processed = (bool)flag!)
                .To("direct://svc-test-out");
        });

        IExchange? received = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://svc-test-out")
                .Process(e => received = e);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://svc-test-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("test")));

        processed.Should().BeTrue();
        received.Should().NotBeNull();
    }

    // ── RouteBuilder subclass gets Context ──

    [Fact]
    public async Task RouteBuilder_Subclass_HasContext()
    {
        IRouteContext? builderContext = null;

        var builder = new ContextCapturingBuilder(ctx => builderContext = ctx);
        _context.AddRoutes(builder);

        await _context.Start();

        builderContext.Should().NotBeNull();
        builderContext.Should().BeSameAs(_context);
    }

    private sealed class ContextCapturingBuilder : RouteBuilder
    {
        private readonly Action<IRouteContext?> _capture;
        public ContextCapturingBuilder(Action<IRouteContext?> capture) => _capture = capture;

        protected override void Configure()
        {
            _capture(Context);
            From("direct://builder-ctx-test")
                .Process(e => { });
        }
    }
}

// ── Extension method helpers for testing ──

internal static class RouteContextTestExtensions
{
    /// <summary>Captures GetContext() at DSL build time.</summary>
    public static IRouteDefinition CaptureContext(this IRouteDefinition rd, Action<IRouteContext?> capture)
    {
        capture(rd.GetContext());
        return rd.Process(e => { }); // no-op
    }

    /// <summary>Extension that reads from registry at build-time, uses value at run-time.</summary>
    public static IRouteDefinition UseRegistryFlag(this IRouteDefinition rd, string key, Action<object?> onFlag)
    {
        var ctx = rd.GetContext()!;
        var flag = ctx.GetFromRegistry<object>(key);
        return rd.Process(e => onFlag(flag));
    }
}
