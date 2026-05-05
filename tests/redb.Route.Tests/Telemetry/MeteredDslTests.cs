using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Processors;
using redb.Route.Telemetry;

namespace redb.Route.Tests.Telemetry;

/// <summary>
/// Tests for the .Metered() DSL — inline delegates, inline processors, and block scopes.
/// Verifies compilation, execution, metric recording, and cardinality protection.
/// </summary>
[Collection("Telemetry")]
public class MeteredDslTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();
    private readonly MeterListener _listener;
    private readonly ConcurrentBag<(string Name, object Value, KeyValuePair<string, object?>[] Tags)> _measurements = [];

    public MeteredDslTests()
    {
        _listener = new MeterListener();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == RouteMetrics.MeterName)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            _measurements.Add((instrument.Name, value, tags.ToArray()));
        });
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            _measurements.Add((instrument.Name, value, tags.ToArray()));
        });
        _listener.Start();
    }

    public async ValueTask DisposeAsync()
    {
        await _context.Stop();
        _listener.Dispose();
    }

    private KeyValuePair<string, object?>[] GetTags(string instrumentName) =>
        _measurements.First(m => m.Name == instrumentName).Tags;

    /// <summary>Filter measurements by step tag name — isolates from cross-test noise.</summary>
    private IEnumerable<(string Name, object Value, KeyValuePair<string, object?>[] Tags)> ForStep(string stepName) =>
        _measurements.Where(m => m.Tags.Any(t => t.Key == "redb.route.step" && (string)t.Value! == stepName));

    // ── Inline delegate (sync) ──

    [Fact]
    public async Task Metered_InlineSync_ExecutesAndRecordsMetrics()
    {
        var processed = false;

        _context.AddRoutes(r =>
        {
            r.From("direct://metered-sync-in")
                .Metered("validate", e => processed = true)
                .To("direct://metered-sync-out");
        });

        _context.AddRoutes(r =>
        {
            r.From("direct://metered-sync-out").Process(e => { });
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://metered-sync-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("test")));

        _listener.RecordObservableInstruments();

        processed.Should().BeTrue();

        var step = ForStep("validate").ToList();
        step.Should().Contain(m => m.Name == "redb.route.step.processed");
        step.Should().Contain(m => m.Name == "redb.route.step.duration");
        step.Should().NotContain(m => m.Name == "redb.route.step.failed");

        var tags = step.First(m => m.Name == "redb.route.step.processed").Tags;
        tags.Should().Contain(t => t.Key == "redb.route.step" && (string)t.Value! == "validate");
    }

    // ── Inline delegate (async) ──

    [Fact]
    public async Task Metered_InlineAsync_ExecutesAndRecordsMetrics()
    {
        var processed = false;

        _context.AddRoutes(r =>
        {
            r.From("direct://metered-async-in")
                .Metered("enrich", async (e, ct) =>
                {
                    await Task.Yield();
                    processed = true;
                })
                .To("direct://metered-async-out");
        });

        _context.AddRoutes(r =>
        {
            r.From("direct://metered-async-out").Process(e => { });
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://metered-async-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("test")));

        _listener.RecordObservableInstruments();

        processed.Should().BeTrue();
        var step = ForStep("enrich").ToList();
        step.Should().Contain(m => m.Name == "redb.route.step.processed");
        step.Should().Contain(m => m.Name == "redb.route.step.duration");
    }

    // ── Inline IProcessor ──

    [Fact]
    public async Task Metered_InlineProcessor_ExecutesAndRecordsMetrics()
    {
        var processed = false;
        var processor = new DelegateProcessor(e => processed = true);

        _context.AddRoutes(r =>
        {
            r.From("direct://metered-proc-in")
                .Metered("transform", processor)
                .To("direct://metered-proc-out");
        });

        _context.AddRoutes(r =>
        {
            r.From("direct://metered-proc-out").Process(e => { });
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://metered-proc-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("test")));

        _listener.RecordObservableInstruments();

        processed.Should().BeTrue();
        ForStep("transform").Should().Contain(m => m.Name == "redb.route.step.processed");
    }

    // ── Block scope ──

    [Fact]
    public async Task Metered_BlockScope_WrapsMultipleSteps()
    {
        var step1 = false;
        var step2 = false;

        _context.AddRoutes(r =>
        {
            r.From("direct://metered-block-in")
                .Metered("pipeline")
                    .Process(e => step1 = true)
                    .Process(e => step2 = true)
                .EndMetered()
                .To("direct://metered-block-out");
        });

        _context.AddRoutes(r =>
        {
            r.From("direct://metered-block-out").Process(e => { });
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://metered-block-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("test")));

        _listener.RecordObservableInstruments();

        step1.Should().BeTrue();
        step2.Should().BeTrue();

        // Should record 1 processed count for entire block, not per inner step
        var step = ForStep("pipeline").ToList();
        step.Where(m => m.Name == "redb.route.step.processed").Should().ContainSingle();
        step.Where(m => m.Name == "redb.route.step.duration").Should().ContainSingle();
    }

    // ── Block scope with End() ──

    [Fact]
    public async Task Metered_BlockScope_EndClosesScope()
    {
        var processed = false;

        _context.AddRoutes(r =>
        {
            r.From("direct://metered-end-in")
                .Metered("step-a")
                    .Process(e => processed = true)
                .End()  // generic End() should close Metered too
                .To("direct://metered-end-out");
        });

        _context.AddRoutes(r =>
        {
            r.From("direct://metered-end-out").Process(e => { });
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://metered-end-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("test")));

        _listener.RecordObservableInstruments();

        processed.Should().BeTrue();
        ForStep("step-a").Should().Contain(m => m.Name == "redb.route.step.processed");
    }

    // ── Failure recording ──

    [Fact]
    public async Task Metered_FailedStep_RecordsFailureMetric()
    {
        _context.AddRoutes(r =>
        {
            r.From("direct://metered-fail-in")
                .Metered("will-fail", e => throw new InvalidOperationException("boom"));
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://metered-fail-in").CreateProducer();
        await producer.Start();

        try
        {
            await producer.Process(new Exchange(new Message("test")));
        }
        catch
        {
            // Expected — exception propagates
        }

        _listener.RecordObservableInstruments();

        var step = ForStep("will-fail").ToList();
        step.Should().Contain(m => m.Name == "redb.route.step.failed");
        step.Should().NotContain(m => m.Name == "redb.route.step.processed");
        step.Should().Contain(m => m.Name == "redb.route.step.duration");

        var tags = step.First(m => m.Name == "redb.route.step.failed").Tags;
        tags.Should().Contain(t => t.Key == "redb.route.step" && (string)t.Value! == "will-fail");
    }

    // ── Duration recording ──

    [Fact]
    public async Task Metered_RecordsDurationInMilliseconds()
    {
        _context.AddRoutes(r =>
        {
            r.From("direct://metered-dur-in")
                .Metered("slow-step", async (e, ct) => await Task.Delay(50, ct))
                .To("direct://metered-dur-out");
        });

        _context.AddRoutes(r =>
        {
            r.From("direct://metered-dur-out").Process(e => { });
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://metered-dur-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("test")));

        _listener.RecordObservableInstruments();

        var duration = _measurements.First(m => m.Name == "redb.route.step.duration");
        ((double)duration.Value).Should().BeGreaterThan(40); // at least ~50ms
    }

    // ── Cardinality protection ──

    [Fact]
    public void Metered_DynamicName_ThrowsArgumentException()
    {
        var rd = new RouteDefinition();
        var act = () => rd.Metered("order-${header.type}");
        act.Should().Throw<ArgumentException>()
            .WithMessage("*cardinality*");
    }

    [Fact]
    public void Metered_NullName_ThrowsException()
    {
        var rd = new RouteDefinition();
        var act = () => rd.Metered(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Metered_EmptyName_ThrowsException()
    {
        var rd = new RouteDefinition();
        var act = () => rd.Metered("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EndMetered_OutsideScope_ThrowsException()
    {
        var rd = new RouteDefinition();
        var act = () => rd.EndMetered();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Metered*");
    }

    // ── Route ID tag ──

    [Fact]
    public async Task Metered_TagsIncludeRouteId()
    {
        _context.AddRoutes(r =>
        {
            r.From("direct://metered-routeid-in")
                .RouteId("order-route")
                .Metered("check", e => { })
                .To("direct://metered-routeid-out");
        });

        _context.AddRoutes(r =>
        {
            r.From("direct://metered-routeid-out").Process(e => { });
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://metered-routeid-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("test")));

        _listener.RecordObservableInstruments();

        var tags = ForStep("check").First(m => m.Name == "redb.route.step.processed").Tags;
        tags.Should().Contain(t => t.Key == "redb.route.id" && ((string)t.Value!).Contains("order-route"));
    }

    // ── Multiple metered steps ──

    [Fact]
    public async Task Metered_MultipleSteps_RecordsSeparateMetrics()
    {
        _context.AddRoutes(r =>
        {
            r.From("direct://metered-multi-in")
                .Metered("step-a", e => { })
                .Metered("step-b", e => { })
                .To("direct://metered-multi-out");
        });

        _context.AddRoutes(r =>
        {
            r.From("direct://metered-multi-out").Process(e => { });
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://metered-multi-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("test")));

        _listener.RecordObservableInstruments();

        var stepA = ForStep("step-a").ToList();
        var stepB = ForStep("step-b").ToList();
        stepA.Should().Contain(m => m.Name == "redb.route.step.processed");
        stepB.Should().Contain(m => m.Name == "redb.route.step.processed");
    }

    // ── Traced + Metered composability ──

    [Fact]
    public async Task Metered_InsideTraced_BothRecord()
    {
        var processed = false;
        using var activityListener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == RouteActivitySource.SourceName,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded
        };
        System.Diagnostics.ActivitySource.AddActivityListener(activityListener);

        _context.AddRoutes(r =>
        {
            r.From("direct://metered-traced-in")
                .Traced("order-span")
                    .Metered("order-validate", e => processed = true)
                .EndTraced()
                .To("direct://metered-traced-out");
        });

        _context.AddRoutes(r =>
        {
            r.From("direct://metered-traced-out").Process(e => { });
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://metered-traced-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("test")));

        _listener.RecordObservableInstruments();

        processed.Should().BeTrue();
        ForStep("order-validate").Should().Contain(m => m.Name == "redb.route.step.processed");
    }
}
