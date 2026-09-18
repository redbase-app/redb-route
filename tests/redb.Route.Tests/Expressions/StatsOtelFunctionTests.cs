using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Telemetry;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// <c>stats('otel:instrument[@route][/step]', field)</c> — the OpenTelemetry layer as values in the
/// expression language (METRICS_IN_ROUTE_PLAN, open question 2). The EIP counters and
/// <c>.Metered()</c> durations live only in that layer, so without this a markup route cannot see
/// them at all. The in-process subscriber stays opt-in; asking for it without
/// <c>UseMetricsSnapshot()</c> is an authoring error and says so.
/// </summary>
[Collection("ExpressionResolver")]
public sealed class StatsOtelFunctionTests
{
    private static async Task<IProducer> Started(RouteContext context, string uri)
    {
        await context.Start();
        var producer = context.GetEndpoint(uri).CreateProducer();
        await producer.Start();
        return producer;
    }

    [Fact]
    public async Task Reads_a_metered_step_of_the_current_route()
    {
        var routeId = $"otel-fn-{Guid.NewGuid():N}";
        await using var context = new RouteContext();
        context.UseMetricsSnapshot();

        string? count = null;
        string? max = null;
        context.AddRoutes(r => r.From("direct:otel-fn-step").RouteId(routeId)
            .Metered("work", _ => { })
            .Process(e =>
            {
                count = ExpressionResolver.ProcessTemplate("${stats('otel:redb.route.step.duration/work', 'count')}", e);
                max = ExpressionResolver.ProcessTemplate("${stats('otel:redb.route.step.duration/work', 'max')}", e);
            }));

        var producer = await Started(context, "direct:otel-fn-step");
        await producer.Process(new Exchange(new Message("x")));

        count.Should().Be("1", "the step ran once on this route");
        max.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Named_route_and_step_can_be_addressed_from_another_route()
    {
        var workerRoute = $"otel-fn-w-{Guid.NewGuid():N}";
        await using var context = new RouteContext();
        context.UseMetricsSnapshot();

        double? seen = null;
        context.AddRoutes(r =>
        {
            r.From("direct:otel-fn-worker").RouteId(workerRoute).Metered("work", _ => { });
            r.From("direct:otel-fn-reader").RouteId($"otel-fn-r-{Guid.NewGuid():N}")
                .Process(e => seen = Convert.ToDouble(
                    ExpressionResolver.ProcessTemplate(
                        $"${{stats('otel:redb.route.step.duration@{workerRoute}/work', 'count')}}", e)));
        });

        var worker = await Started(context, "direct:otel-fn-worker");
        await worker.Process(new Exchange(new Message("x")));
        await worker.Process(new Exchange(new Message("x")));

        var reader = context.GetEndpoint("direct:otel-fn-reader").CreateProducer();
        await reader.Start();
        await reader.Process(new Exchange(new Message("x")));

        seen.Should().Be(2);
    }

    [Fact]
    public async Task An_instrument_nothing_recorded_reads_as_zero()
    {
        await using var context = new RouteContext();
        context.UseMetricsSnapshot();

        string? rendered = null;
        context.AddRoutes(r => r.From("direct:otel-fn-empty").RouteId($"otel-fn-e-{Guid.NewGuid():N}")
            .Process(e => rendered = ExpressionResolver.ProcessTemplate(
                "${stats('otel:redb.route.throttle.delayed', 'sum')}", e)));

        var producer = await Started(context, "direct:otel-fn-empty");
        await producer.Process(new Exchange(new Message("x")));

        rendered.Should().Be("0", "an instrument with no measurements is zero, not a failure");
    }

    [Fact]
    public async Task Without_the_subscriber_it_fails_loudly()
    {
        await using var context = new RouteContext();   // no UseMetricsSnapshot()
        Exception? captured = null;
        context.AddRoutes(r => r.From("direct:otel-fn-off").RouteId($"otel-fn-o-{Guid.NewGuid():N}")
            .Process(e =>
            {
                try { ExpressionResolver.ProcessTemplate("${stats('otel:redb.route.step.duration/work', 'count')}", e); }
                catch (Exception ex) { captured = ex; }
            }));

        var producer = await Started(context, "direct:otel-fn-off");
        await producer.Process(new Exchange(new Message("x")));

        captured.Should().NotBeNull();
        captured!.Message.Should().Contain("UseMetricsSnapshot");
    }

    [Fact]
    public async Task An_unknown_field_names_the_known_ones()
    {
        await using var context = new RouteContext();
        context.UseMetricsSnapshot();
        Exception? captured = null;
        context.AddRoutes(r => r.From("direct:otel-fn-bad").RouteId($"otel-fn-b-{Guid.NewGuid():N}")
            .Process(e =>
            {
                try { ExpressionResolver.ProcessTemplate("${stats('otel:redb.route.step.duration/work', 'median')}", e); }
                catch (Exception ex) { captured = ex; }
            }));

        var producer = await Started(context, "direct:otel-fn-bad");
        await producer.Process(new Exchange(new Message("x")));

        captured.Should().NotBeNull();
        captured!.Message.Should().Contain("median").And.Contain("count");
    }

    [Fact]
    public async Task A_literal_unknown_field_fails_while_the_route_is_built()
    {
        await using var context = new RouteContext();
        context.UseMetricsSnapshot();
        context.AddRoutes(r => r.From("direct:otel-fn-build").RouteId("otel-fn-build")
            .Filter("stats('otel:redb.route.step.duration/work', 'median') > 0")
                .To("direct:otel-fn-sink")
            .EndFilter());

        var act = () => context.Start();

        (await act.Should().ThrowAsync<Exception>())
            .WithMessage("*median*",
                "a condition that can never work fails at build, not on the first message");
    }

    [Fact]
    public void An_endpoint_metric_still_reads_as_before()
    {
        RedbRouteStatsNameCheck.KnownEndpointMetricsStillAccepted();
    }

    /// <summary>Guard: the endpoint branch of stats() keeps its own metric names.</summary>
    private static class RedbRouteStatsNameCheck
    {
        public static void KnownEndpointMetricsStillAccepted()
        {
            ExpressionResolver.IsKnownStatistic("messagesIn").Should().BeTrue();
            ExpressionResolver.IsKnownStatistic("max").Should().BeFalse("max belongs to the otel: branch");
        }
    }
}
