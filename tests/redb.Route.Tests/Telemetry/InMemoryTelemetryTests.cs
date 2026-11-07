using System.Diagnostics;
using NSubstitute;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.ErrorHandling;
using redb.Route.Processors;
using redb.Route.Telemetry;
using Xunit;

namespace redb.Route.Tests.Telemetry;

/// <summary>
/// Comprehensive telemetry tests using the OpenTelemetry SDK in-memory exporters.
/// Verifies both new metric instruments declared in <see cref="ProcessorMetrics"/>
/// and transport spans opened via <see cref="RouteTelemetryExtensions"/>.
/// </summary>
[Collection("Telemetry")]
public sealed class InMemoryTelemetryTests : IDisposable
{
    private readonly List<Metric> _metrics = [];
    private readonly List<Activity> _activities = [];
    private readonly MeterProvider _meterProvider;
    private readonly TracerProvider _tracerProvider;

    public InMemoryTelemetryTests()
    {
        _meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(RouteMetrics.MeterName)
            .AddInMemoryExporter(_metrics)
            .Build()!;

        _tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(RouteActivitySource.SourceName)
            .AddInMemoryExporter(_activities)
            .Build()!;
    }

    public void Dispose()
    {
        _meterProvider.Dispose();
        _tracerProvider.Dispose();
    }

    private long Sum(string name)
    {
        _meterProvider.ForceFlush(1000);
        long total = 0;
        foreach (var m in _metrics.Where(x => x.Name == name))
            foreach (var point in m.GetMetricPoints())
            {
                total += m.MetricType switch
                {
                    MetricType.LongSum => point.GetSumLong(),
                    MetricType.LongSumNonMonotonic => point.GetSumLong(),
                    MetricType.Histogram => point.GetHistogramCount(),
                    _ => 0
                };
            }
        return total;
    }

    private static IExchange NewExchange(string body = "x") => new Exchange(new Message(body));

    // ─────────────────────────────────────────────────────────────────────
    //  Telemetry name is shared by Meter and ActivitySource
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void TelemetryName_IsShared_BetweenMeterAndActivitySource()
    {
        RouteMetrics.MeterName.Should().Be(RouteActivitySource.SourceName);
        RouteMetrics.MeterName.Should().Be(RouteActivitySource.TelemetryName);
        RouteMetrics.MeterName.Should().Be("redb.Route");
    }

    // ─────────────────────────────────────────────────────────────────────
    //  WireTap
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WireTap_SuccessfulBranch_IncrementsDispatched()
    {
        var tap = Substitute.For<IProcessor>();
        tap.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var wt = new WireTapProcessor(tap);

        await wt.Process(NewExchange());
        // WireTap is fire-and-forget; allow the background task to run.
        await Task.Delay(100);

        Sum("redb.route.wiretap.dispatched").Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task WireTap_FailingBranch_IncrementsFailed_AndDoesNotPropagate()
    {
        var tap = Substitute.For<IProcessor>();
        tap.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("boom")));
        var wt = new WireTapProcessor(tap);

        Func<Task> act = async () => await wt.Process(NewExchange());
        await act.Should().NotThrowAsync();
        await Task.Delay(100);

        Sum("redb.route.wiretap.failed").Should().BeGreaterThanOrEqualTo(1);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Multicast
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Multicast_RecordsBranchesCount()
    {
        var mc = new MulticastProcessor(parallelProcessing: false);
        mc.AddTarget(Substitute.For<IProcessor>());
        mc.AddTarget(Substitute.For<IProcessor>());
        mc.AddTarget(Substitute.For<IProcessor>());

        await mc.Process(NewExchange());

        Sum("redb.route.multicast.branches").Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Multicast_FailingBranch_IncrementsFailedBranches()
    {
        var ok = Substitute.For<IProcessor>();
        var bad = Substitute.For<IProcessor>();
        bad.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("bad")));

        var mc = new MulticastProcessor(parallelProcessing: false);
        mc.AddTarget(ok);
        mc.AddTarget(bad);

        await mc.Process(NewExchange());

        Sum("redb.route.multicast.failed_branches").Should().BeGreaterThanOrEqualTo(1);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Idempotent consumer
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Idempotent_NewKey_IncrementsPassed()
    {
        var inner = Substitute.For<IProcessor>();
        var repo = new InMemoryIdempotentRepository();
        var idem = new IdempotentConsumerProcessor(inner, repo, ex => "k-new");

        await idem.Process(NewExchange());

        Sum("redb.route.idempotent.passed").Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Idempotent_DuplicateKey_IncrementsDuplicate()
    {
        var inner = Substitute.For<IProcessor>();
        var repo = new InMemoryIdempotentRepository();
        var idem = new IdempotentConsumerProcessor(inner, repo, ex => "dup-key");

        await idem.Process(NewExchange());
        await idem.Process(NewExchange()); // duplicate

        Sum("redb.route.idempotent.duplicate").Should().BeGreaterThanOrEqualTo(1);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Retry
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Retry_EventualSuccess_IncrementsAttemptsAndSuccess()
    {
        var calls = 0;
        var inner = Substitute.For<IProcessor>();
        inner.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls++;
                return calls < 3 ? Task.FromException(new InvalidOperationException("transient")) : Task.CompletedTask;
            });
        var policy = new RetryPolicy
        {
            MaxRetries = 5,
            InitialDelay = TimeSpan.Zero,
            MaxDelay = TimeSpan.Zero
        };
        var retry = new RetryProcessor(inner, policy);

        await retry.Process(NewExchange());

        Sum("redb.route.retry.attempts").Should().BeGreaterThanOrEqualTo(2);
        Sum("redb.route.retry.success").Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Retry_ExhaustsAttempts_IncrementsExhausted()
    {
        var inner = Substitute.For<IProcessor>();
        inner.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("always-fails")));
        var policy = new RetryPolicy
        {
            MaxRetries = 2,
            InitialDelay = TimeSpan.Zero,
            MaxDelay = TimeSpan.Zero
        };
        var retry = new RetryProcessor(inner, policy);

        var act = () => retry.Process(NewExchange());
        await act.Should().ThrowAsync<InvalidOperationException>();

        Sum("redb.route.retry.exhausted").Should().BeGreaterThanOrEqualTo(1);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Transport span helper
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void StartTransportSpan_EmitsSemanticTags()
    {
        using (var act = RouteTelemetryExtensions.StartTransportSpan(
            "my-queue publish", ActivityKind.Producer,
            "messaging.system", "rabbitmq",
            "rabbitmq://localhost/my-queue",
            destination: "my-queue",
            operation: "publish"))
        {
            act.Should().NotBeNull();
        }

        _tracerProvider.ForceFlush(1000);

        _activities.Should().ContainSingle(a => a.OperationName == "my-queue publish");
        var span = _activities.Single(a => a.OperationName == "my-queue publish");
        span.Kind.Should().Be(ActivityKind.Producer);
        span.Tags.Should().Contain(t => t.Key == "messaging.system" && t.Value == "rabbitmq");
        span.Tags.Should().Contain(t => t.Key == "messaging.destination.name" && t.Value == "my-queue");
        span.Tags.Should().Contain(t => t.Key == "messaging.operation" && t.Value == "publish");
        span.Tags.Should().Contain(t => t.Key == "redb.route.endpoint");
    }

    // ─────────────────────────────────────────────────────────────────────
    //  MeteredProcessor tag enrichment
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MeteredProcessor_EmitsEndpointAndSchemeTags()
    {
        var inner = Substitute.For<IProcessor>();
        var metered = new MeteredProcessor(
            inner,
            routeId: "test-route",
            endpointUri: "http://example.com",
            endpointScheme: "http");

        await metered.Process(NewExchange());

        _meterProvider.ForceFlush(1000);

        var processed = _metrics.SingleOrDefault(m => m.Name == "redb.route.exchanges.processed");
        processed.Should().NotBeNull();

        var found = false;
        foreach (var point in processed!.GetMetricPoints())
        {
            var endpointSeen = false;
            var schemeSeen = false;
            var routeIdSeen = false;
            foreach (var tag in point.Tags)
            {
                if (tag.Key == "redb.route.endpoint" && Equals(tag.Value, "http://example.com")) endpointSeen = true;
                if (tag.Key == "redb.route.scheme" && Equals(tag.Value, "http")) schemeSeen = true;
                if (tag.Key == "redb.route.id" && Equals(tag.Value, "test-route")) routeIdSeen = true;
            }
            if (endpointSeen && schemeSeen && routeIdSeen) { found = true; break; }
        }
        found.Should().BeTrue("MeteredProcessor must emit redb.route.id, redb.route.endpoint and redb.route.scheme tags");
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Activity exception event (InstrumentedProcessor.ActivityExtensions)
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InstrumentedProcessor_OnFailure_RecordsExceptionEventOnSpan()
    {
        var inner = Substitute.For<IProcessor>();
        inner.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("kaboom")));

        var ip = new InstrumentedProcessor(inner, "route:test");
        var act = () => ip.Process(NewExchange());
        await act.Should().ThrowAsync<InvalidOperationException>();

        _tracerProvider.ForceFlush(1000);

        var span = _activities.SingleOrDefault(a => a.OperationName == "route:test");
        span.Should().NotBeNull();
        span!.Events.Should().Contain(e => e.Name == "exception");
    }
}
