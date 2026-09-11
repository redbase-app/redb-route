using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.Tests.Telemetry;

/// <summary>
/// <see cref="IMetricsSnapshot"/> — the opt-in, in-process subscriber that makes the OpenTelemetry
/// layer readable (METRICS_IN_ROUTE_PLAN, П3). The <c>"redb.Route"</c> meter is process-global, so
/// keys carry the route id and step tags; tests use unique route ids to stay isolated from
/// whatever else the test process measures.
/// </summary>
public sealed class MetricsSnapshotTests
{
    private static async Task<IProducer> Started(RouteContext context, string uri)
    {
        await context.Start();
        var producer = context.GetEndpoint(uri).CreateProducer();
        await producer.Start();
        return producer;
    }

    [Fact]
    public void Without_registration_there_is_no_snapshot_and_no_listener_tax()
    {
        new RouteContext().GetMetricsSnapshot().Should().BeNull(
            "nobody pays for a per-measurement callback without asking for it");
    }

    [Fact]
    public async Task A_metered_step_becomes_readable_from_the_process()
    {
        var routeId = $"snap-{Guid.NewGuid():N}";
        await using var context = new RouteContext();
        context.UseMetricsSnapshot();
        context.AddRoutes(r => r.From("direct:snap-step").RouteId(routeId)
            .Metered("work", _ => { }));

        var producer = await Started(context, "direct:snap-step");
        for (var i = 0; i < 3; i++)
            await producer.Process(new Exchange(new Message("m")));

        var snapshot = context.GetMetricsSnapshot()!;
        snapshot.Value("redb.route.step.processed", routeId, "work").Should().Be(3);

        var duration = snapshot.Point("redb.route.step.duration", routeId, "work");
        duration.Should().NotBeNull();
        duration!.Value.Count.Should().Be(3);
        duration.Value.Min.Should().BeLessThanOrEqualTo(duration.Value.Max);
    }

    [Fact]
    public async Task Parallel_measurements_are_not_lost()
    {
        var routeId = $"snap-par-{Guid.NewGuid():N}";
        await using var context = new RouteContext();
        context.UseMetricsSnapshot();
        context.AddRoutes(r => r.From("direct:snap-par").RouteId(routeId)
            .Metered("hot", _ => { }));

        var producer = await Started(context, "direct:snap-par");
        await Parallel.ForEachAsync(
            Enumerable.Range(0, 800),
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            async (_, ct) => await producer.Process(new Exchange(new Message("m")), ct));

        context.GetMetricsSnapshot()!.Value("redb.route.step.processed", routeId, "hot")
            .Should().Be(800, "the accumulator must not drop concurrent increments");
    }

    [Fact]
    public async Task Registering_twice_keeps_one_subscriber()
    {
        await using var context = new RouteContext();
        context.UseMetricsSnapshot();
        var first = context.GetMetricsSnapshot();
        context.UseMetricsSnapshot();

        context.GetMetricsSnapshot().Should().BeSameAs(first);
    }

    [Fact]
    public async Task Collected_points_survive_the_context_stopping()
    {
        var routeId = $"snap-stop-{Guid.NewGuid():N}";
        var context = new RouteContext();
        context.UseMetricsSnapshot();
        context.AddRoutes(r => r.From("direct:snap-stop").RouteId(routeId)
            .Metered("tail", _ => { }));

        var producer = await Started(context, "direct:snap-stop");
        await producer.Process(new Exchange(new Message("m")));

        var snapshot = context.GetMetricsSnapshot()!;
        await context.DisposeAsync();

        snapshot.Value("redb.route.step.processed", routeId, "tail").Should().Be(1,
            "an operator inspecting a stopped context still sees what it measured");
    }
}
