using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;

namespace redb.Route.Tests.Definitions;

/// <summary>
/// Tests for W5 F10 — RouteBuilder (class-based and inline) factory.
/// </summary>
public class RouteBuilderTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    // ── Helper builder subclass ────────────────────────────────────────────────

    private sealed class SingleRouteBuilder : RouteBuilder
    {
        private readonly string _from;
        private readonly Action<IRouteDefinition> _configure;

        public SingleRouteBuilder(string from, Action<IRouteDefinition> configure)
        {
            _from = from;
            _configure = configure;
        }

        protected override void Configure() => _configure(From(_from));
    }

    private sealed class TwoRouteBuilder : RouteBuilder
    {
        private readonly List<string> _hits;
        public TwoRouteBuilder(List<string> hits) => _hits = hits;

        protected override void Configure()
        {
            From("direct://rb2-multi-1").Process(e => _hits.Add("1:" + e.In.Body));
            From("direct://rb2-multi-2").Process(e => _hits.Add("2:" + e.In.Body));
        }
    }

    // ── RouteBuilder class-based ─────────────────────────────────────────────

    [Fact]
    public async Task ClassBased_SingleRoute_ProcessesExchange()
    {
        IExchange? captured = null;
        var builder = new SingleRouteBuilder(
            "direct://rb2-class",
            r => r.Process(e => captured = e).SetBody("rb2"));

        _context.AddRoutes(builder);
        await _context.Start();

        var producer = _context.GetEndpoint("direct://rb2-class").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message { Body = "in" }));

        captured.Should().NotBeNull();
        captured!.In.Body.Should().Be("rb2");
    }

    [Fact]
    public async Task ClassBased_MultipleRoutes_BothProcess()
    {
        var hits = new List<string>();

        _context.AddRoutes(new TwoRouteBuilder(hits));
        await _context.Start();

        var p1 = _context.GetEndpoint("direct://rb2-multi-1").CreateProducer();
        var p2 = _context.GetEndpoint("direct://rb2-multi-2").CreateProducer();
        await p1.Start(); await p2.Start();

        await p1.Process(new Exchange(new Message { Body = "a" }));
        await p2.Process(new Exchange(new Message { Body = "b" }));

        hits.Should().BeEquivalentTo(["1:a", "2:b"]);
    }

    [Fact]
    public async Task ClassBased_RouteId_Preserved()
    {
        var builder = new SingleRouteBuilder(
            "direct://rb2-id",
            r => r.RouteId("my-builder2-route").SetBody("ok"));

        _context.AddRoutes(builder);
        await _context.Start();

        _context.GetRoute("my-builder2-route").Should().NotBeNull();
    }

    [Fact]
    public async Task ClassBased_AutoStartFalse_RouteStopped()
    {
        var builder = new SingleRouteBuilder(
            "direct://rb2-stopped",
            r => r.RouteId("rb2-no-start").AutoStart(false).SetBody("x"));

        _context.AddRoutes(builder);
        await _context.Start();

        _context.GetRoute("rb2-no-start")!.Status.Should().Be(RouteStatus.Stopped);
    }

    // ── AddRoutes inline ──────────────────────────────────────────────────────

    [Fact]
    public async Task Inline_AddRoutes_ProcessesExchange()
    {
        IExchange? captured = null;

        _context.AddRoutes(r =>
        {
            r.From("direct://rb2-inline")
                .SetBody("inline-ok")
                .Process(e => captured = e);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://rb2-inline").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message { Body = "x" }));

        captured!.In.Body.Should().Be("inline-ok");
    }

    [Fact]
    public async Task Inline_AddRoutes_MultipleRoutes()
    {
        var hits = new List<string>();

        _context.AddRoutes(r =>
        {
            r.From("direct://rb2-il-a").Process(e => hits.Add("A"));
            r.From("direct://rb2-il-b").Process(e => hits.Add("B"));
        });

        await _context.Start();

        var pa = _context.GetEndpoint("direct://rb2-il-a").CreateProducer();
        var pb = _context.GetEndpoint("direct://rb2-il-b").CreateProducer();
        await pa.Start(); await pb.Start();

        await pa.Process(new Exchange(new Message()));
        await pb.Process(new Exchange(new Message()));

        hits.Should().BeEquivalentTo(["A", "B"]);
    }

    [Fact]
    public async Task Inline_AddRoutes_WithScopes()
    {
        var passed = new List<object?>();

        _context.AddRoutes(r =>
        {
            r.From("direct://rb2-scope")
                .Filter(e => e.In.Body is int n && n > 5)
                    .Process(e => passed.Add(e.In.Body))
                    .EndFilter();
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://rb2-scope").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message { Body = 3 }));
        await producer.Process(new Exchange(new Message { Body = 7 }));
        await producer.Process(new Exchange(new Message { Body = 10 }));

        passed.Should().BeEquivalentTo([7, 10]);
    }

    // ── AddRoutes(RouteBuilder) null guard ───────────────────────────────────

    [Fact]
    public void AddRoutes_NullBuilder2_Throws()
    {
        _context.Invoking(c => c.AddRoutes((RouteBuilder)null!))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddRoutes_NullAction_Throws()
    {
        _context.Invoking(c => c.AddRoutes((Action<InlineRouteBuilder>)null!))
            .Should().Throw<ArgumentNullException>();
    }

    // ── OnException via RouteBuilder2BatchBridge ───────────────────────────────

    private sealed class BuilderWithOnException : RouteBuilder
    {
        private readonly List<string> _log;
        public BuilderWithOnException(List<string> log) => _log = log;

        protected override void Configure()
        {
            OnException<InvalidOperationException>()
                .Handled()
                .Process(e => _log.Add("caught:" + e.Exception?.Message));

            From("direct://rb2-ex")
                .Process(_ => throw new InvalidOperationException("boom"))
                .Process(e => _log.Add("should-not-reach"));
        }
    }

    [Fact]
    public async Task OnException_InRouteBuilder2_HandlesException()
    {
        var log = new List<string>();
        _context.AddRoutes(new BuilderWithOnException(log));
        await _context.Start();

        var producer = _context.GetEndpoint("direct://rb2-ex").CreateProducer();
        await producer.Start();

        // Should NOT throw because the exception is handled
        await producer.Process(new Exchange(new Message { Body = "test" }));

        log.Should().ContainSingle(s => s.StartsWith("caught:boom"));
        log.Should().NotContain("should-not-reach");
    }
}
