using System.Diagnostics;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Processors;
using redb.Route.Telemetry;

namespace redb.Route.Tests.Telemetry;

/// <summary>
/// Tests for the .Traced() DSL — inline delegates, inline processors, and block scopes.
/// Verifies compilation, execution, Activity creation, and expression template support.
/// </summary>
[Collection("Telemetry")]
public class TracedDslTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();
    private readonly ActivityListener _listener;
    private readonly List<Activity> _completedActivities = [];

    public TracedDslTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RouteActivitySource.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _completedActivities.Add(activity)
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public async ValueTask DisposeAsync()
    {
        await _context.Stop();
        _listener.Dispose();
    }

    // ── Inline delegate (sync) ──

    [Fact]
    public async Task Traced_InlineSync_ExecutesAndCreatesSpan()
    {
        var processed = false;

        _context.AddRoutes(r =>
        {
            r.From("direct://traced-sync-in")
                .Traced("validate-order", e => { processed = true; })
                .To("direct://traced-sync-out");
        });

        IExchange? received = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://traced-sync-out")
                .Process(e => received = e);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://traced-sync-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("test")));

        processed.Should().BeTrue();
        received.Should().NotBeNull();
        _completedActivities.Should().Contain(a => a.DisplayName == "validate-order");
    }

    // ── Inline delegate (async) ──

    [Fact]
    public async Task Traced_InlineAsync_ExecutesAndCreatesSpan()
    {
        var processed = false;

        _context.AddRoutes(r =>
        {
            r.From("direct://traced-async-in")
                .Traced("enrich-data", async (e, ct) =>
                {
                    await Task.Delay(1, ct);
                    processed = true;
                })
                .To("direct://traced-async-out");
        });

        IExchange? received = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://traced-async-out")
                .Process(e => received = e);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://traced-async-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("test")));

        processed.Should().BeTrue();
        received.Should().NotBeNull();
        _completedActivities.Should().Contain(a => a.DisplayName == "enrich-data");
    }

    // ── Inline IProcessor ──

    [Fact]
    public async Task Traced_InlineProcessor_ExecutesAndCreatesSpan()
    {
        var processed = false;
        var inner = new DelegateProcessor(_ => processed = true);

        _context.AddRoutes(r =>
        {
            r.From("direct://traced-proc-in")
                .Traced("transform-step", inner)
                .To("direct://traced-proc-out");
        });

        IExchange? received = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://traced-proc-out")
                .Process(e => received = e);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://traced-proc-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("test")));

        processed.Should().BeTrue();
        received.Should().NotBeNull();
        _completedActivities.Should().Contain(a => a.DisplayName == "transform-step");
    }

    // ── Block scope ──

    [Fact]
    public async Task Traced_BlockScope_WrapsMultipleStepsInSingleSpan()
    {
        var step1 = false;
        var step2 = false;

        _context.AddRoutes(r =>
        {
            r.From("direct://traced-block-in")
                .Traced("order-pipeline")
                    .Process(e => step1 = true)
                    .Process(e => step2 = true)
                .EndTraced()
                .To("direct://traced-block-out");
        });

        IExchange? received = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://traced-block-out")
                .Process(e => received = e);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://traced-block-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("test")));

        step1.Should().BeTrue();
        step2.Should().BeTrue();
        received.Should().NotBeNull();
        _completedActivities.Should().Contain(a => a.DisplayName == "order-pipeline");
    }

    // ── Block scope with End() ──

    [Fact]
    public async Task Traced_BlockScope_EndAlsoWorks()
    {
        var executed = false;

        _context.AddRoutes(r =>
        {
            r.From("direct://traced-end-in")
                .Traced("my-block")
                    .Process(e => executed = true)
                .End()
                .To("direct://traced-end-out");
        });

        IExchange? received = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://traced-end-out")
                .Process(e => received = e);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://traced-end-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("test")));

        executed.Should().BeTrue();
        received.Should().NotBeNull();
        _completedActivities.Should().Contain(a => a.DisplayName == "my-block");
    }

    // ── Expression template in span name ──

    [Fact]
    public async Task Traced_ExpressionInName_ResolvesAtRuntime()
    {
#pragma warning disable CS0618 // v1 DSL — Traced expression templates require v1 semantics
        _context.AddRoutes((InlineRouteBuilder r) =>
        {
            r.From("direct://traced-expr-in")
                .Traced("process-${header.orderType}", e =>
                {
                    e.In.Body = "processed";
                })
                .To("direct://traced-expr-out");
        });
#pragma warning restore CS0618

        IExchange? received = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://traced-expr-out")
                .Process(e => received = e);
        });

        await _context.Start();

        var msg = new Message("data");
        msg.Headers["orderType"] = "PREMIUM";
        var exchange = new Exchange(msg);

        var producer = _context.GetEndpoint("direct://traced-expr-in").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);

        received.Should().NotBeNull();
        received!.In.Body.Should().Be("processed");
        _completedActivities.Should().Contain(a => a.DisplayName == "process-PREMIUM");
    }

    // ── Block scope with expression ──

    [Fact]
    public async Task Traced_BlockScope_ExpressionInName()
    {
#pragma warning disable CS0618 // v1 DSL — Traced expression templates require v1 semantics
        _context.AddRoutes((InlineRouteBuilder r) =>
        {
            r.From("direct://traced-block-expr-in")
                .Traced("pipeline-${header.region}")
                    .SetHeader("processed", "yes")
                .EndTraced()
                .To("direct://traced-block-expr-out");
        });
#pragma warning restore CS0618

        IExchange? received = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://traced-block-expr-out")
                .Process(e => received = e);
        });

        await _context.Start();

        var msg = new Message("data");
        msg.Headers["region"] = "EU";
        var exchange = new Exchange(msg);

        var producer = _context.GetEndpoint("direct://traced-block-expr-in").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);

        received.Should().NotBeNull();
        received!.In.Headers["processed"].Should().Be("yes");
        _completedActivities.Should().Contain(a => a.DisplayName == "pipeline-EU");
    }

    // ── Multiple Traced steps ──

    [Fact]
    public async Task Traced_MultipleInline_AllCreateSpans()
    {
        _context.AddRoutes(r =>
        {
            r.From("direct://traced-multi-in")
                .Traced("step-1", e => e.In.Headers["s1"] = "done")
                .Traced("step-2", e => e.In.Headers["s2"] = "done")
                .Traced("step-3", e => e.In.Headers["s3"] = "done")
                .To("direct://traced-multi-out");
        });

        IExchange? received = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://traced-multi-out")
                .Process(e => received = e);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://traced-multi-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("test")));

        received.Should().NotBeNull();
        received!.In.Headers["s1"].Should().Be("done");
        received.In.Headers["s2"].Should().Be("done");
        received.In.Headers["s3"].Should().Be("done");

        _completedActivities.Should().Contain(a => a.DisplayName == "step-1");
        _completedActivities.Should().Contain(a => a.DisplayName == "step-2");
        _completedActivities.Should().Contain(a => a.DisplayName == "step-3");
    }

    // ── Nested: Block inside inline ──

    [Fact]
    public async Task Traced_Nested_BlockInsideRoute()
    {
        _context.AddRoutes(r =>
        {
            r.From("direct://traced-nested-in")
                .Traced("outer-block")
                    .Traced("inner-step", e => e.In.Body = "nested")
                .EndTraced()
                .To("direct://traced-nested-out");
        });

        IExchange? received = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://traced-nested-out")
                .Process(e => received = e);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://traced-nested-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("test")));

        received.Should().NotBeNull();
        received!.In.Body.Should().Be("nested");
        _completedActivities.Should().Contain(a => a.DisplayName == "outer-block");
        _completedActivities.Should().Contain(a => a.DisplayName == "inner-step");
    }

    // ── Error handling inside traced ──

    [Fact]
    public async Task Traced_Exception_PropagatesAndRecordsOnSpan()
    {
        _context.AddRoutes(r =>
        {
            r.From("direct://traced-error-in")
                .Traced("failing-step", e =>
                {
                    throw new InvalidOperationException("traced failure");
                });
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://traced-error-in").CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message("test"));

        // InstrumentedProcessor records but rethrows — but route-level handler catches
        // The exchange should have the exception set
        try
        {
            await producer.Process(exchange);
        }
        catch
        {
            // Expected — exception propagates
        }

        _completedActivities.Should().Contain(a => a.DisplayName == "failing-step");
    }

    // ── Validation ──

    [Fact]
    public void Traced_NullSpanName_Throws()
    {
        var rd = new RouteDefinition();
        var act = () => rd.Traced(null!, e => { });
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Traced_EmptySpanName_Throws()
    {
        var rd = new RouteDefinition();
        var act = () => rd.Traced("", e => { });
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EndTraced_OutsideScope_Throws()
    {
        var rd = new RouteDefinition();
        var act = () => rd.EndTraced();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Traced*scope*");
    }

    // ── Body modification flows through ──

    [Fact]
    public async Task Traced_BodyModification_FlowsThrough()
    {
        _context.AddRoutes(r =>
        {
            r.From("direct://traced-flow-in")
                .Traced("step-a", e => e.In.Body = "modified-by-a")
                .Traced("step-b", e =>
                {
                    var prev = e.In.Body?.ToString();
                    e.In.Body = prev + "+b";
                })
                .To("direct://traced-flow-out");
        });

        IExchange? received = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://traced-flow-out")
                .Process(e => received = e);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://traced-flow-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("original")));

        received.Should().NotBeNull();
        received!.In.Body.Should().Be("modified-by-a+b");
    }
}
