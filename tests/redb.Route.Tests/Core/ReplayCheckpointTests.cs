using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// End-to-end tests for replay checkpoints (<c>.Replayable</c> save-points): snapshot capture on
/// pass, tail replay from a snapshot via <see cref="IRouteContext.ReplayAsync"/>, prefix is NOT
/// re-run, marker registry, duplicate detection, graceful degrade for a non-snapshot-able body.
/// </summary>
public class ReplayCheckpointTests : IAsyncDisposable
{
    private readonly RouteContext _ctx = new("replay-test");

    public async ValueTask DisposeAsync()
    {
        await _ctx.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static string Body(IExchange ex) => (string)ex.In.Body!;

    private async Task Send(string uri, object body)
    {
        var pt = new ProducerTemplate(_ctx);
        pt.Start();
        await pt.SendAsync(uri, body);
    }

    [Fact]
    public async Task Marker_CapturesSnapshotIntoRouteCheckpoint_OnPass()
    {
        IExchange? seen = null;
        _ctx.AddRoutes(r => r
            .From("direct:cap").RouteId("cap")
            .Replayable("m1")
            .Process(ex => seen = ex));

        await _ctx.Start();
        await Send("direct:cap", "hello");

        seen.Should().NotBeNull();
        var cp = seen!.Properties[RouteCheckpoint.PropertyKey].Should().BeOfType<RouteCheckpoint>().Subject;
        cp.RouteId.Should().Be("cap");
        cp.MarkerName.Should().Be("m1");
        Body(cp.Snapshot).Should().Be("hello");
    }

    [Fact]
    public async Task Replay_RunsTailFromSnapshot_WithoutRerunningPrefix()
    {
        var trail = new List<string>();
        _ctx.AddRoutes(r => r
            .From("direct:pay").RouteId("pay")
            .Process(_ => trail.Add("charge"))          // prefix (a side effect we must NOT repeat)
            .Replayable("after-charge")
            .Process(_ => trail.Add("receipt")));        // tail (the part replay re-runs)

        await _ctx.Start();
        await Send("direct:pay", "order-1");

        trail.Should().Equal("charge", "receipt");

        // Replay from a fresh snapshot: only the tail should run again.
        var snapshot = new Exchange(new Message("order-1"));
        await _ctx.ReplayAsync("pay", "after-charge", snapshot);

        trail.Should().Equal("charge", "receipt", "receipt");   // charge NOT repeated
    }

    [Fact]
    public async Task Replay_FromCapturedCheckpointSnapshot_Works()
    {
        var tailRuns = 0;
        IExchange? captured = null;
        _ctx.AddRoutes(r => r
            .From("direct:cap2").RouteId("cap2")
            .Process(ex => captured = ex)
            .Replayable("mk")
            .Process(_ => tailRuns++));

        await _ctx.Start();
        await Send("direct:cap2", "x");
        tailRuns.Should().Be(1);

        var cp = (RouteCheckpoint)captured!.Properties[RouteCheckpoint.PropertyKey];
        await _ctx.ReplayAsync(cp.RouteId, cp.MarkerName, cp.Snapshot);

        tailRuns.Should().Be(2);
    }

    [Fact]
    public async Task ImplicitMarker_AtRouteStart_BodyIsWholePipeline_NoEndNeeded()
    {
        var runs = 0;
        _ctx.AddRoutes(r => r
            .From("direct:impl").RouteId("impl")
            .Replayable("start")             // no End — body = whole pipeline
            .Process(_ => runs++));

        await _ctx.Start();

        _ctx.GetReplayMarkers().Should().Contain(("impl", "start"));

        await _ctx.ReplayAsync("impl", "start", new Exchange(new Message("y")));
        runs.Should().Be(1);
    }

    [Fact]
    public async Task LastMarkerWins_WhenSeveralArePassed()
    {
        IExchange? seen = null;
        _ctx.AddRoutes(r => r
            .From("direct:multi").RouteId("multi")
            .Replayable("m1").Process(_ => { }).EndReplayable()
            .Replayable("m2").Process(ex => seen = ex));

        await _ctx.Start();
        await Send("direct:multi", "z");

        ((RouteCheckpoint)seen!.Properties[RouteCheckpoint.PropertyKey]).MarkerName.Should().Be("m2");
        _ctx.GetReplayMarkers().Select(m => m.MarkerName).Should().Contain(new[] { "m1", "m2" });
    }

    [Fact]
    public async Task LambdaBody_Works()
    {
        var runs = 0;
        _ctx.AddRoutes(r => r
            .From("direct:lam").RouteId("lam")
            .Process(_ => { })
            .Replayable("mk", b => b.Process(_ => runs++)));

        await _ctx.Start();
        await _ctx.ReplayAsync("lam", "mk", new Exchange(new Message("q")));
        runs.Should().Be(1);
    }

    [Fact]
    public async Task ReplayAsync_UnknownMarker_Throws()
    {
        _ctx.AddRoutes(r => r.From("direct:u").RouteId("u").Replayable("known").Process(_ => { }));
        await _ctx.Start();

        var act = () => _ctx.ReplayAsync("u", "nope", new Exchange(new Message("x")));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*No replay checkpoint*");
    }

    [Fact]
    public async Task DuplicateMarkerName_InRoute_FailsCompilation()
    {
        _ctx.AddRoutes(r => r
            .From("direct:dup").RouteId("dup")
            .Replayable("same").Process(_ => { }).EndReplayable()
            .Replayable("same").Process(_ => { }));

        var act = () => _ctx.Start();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Duplicate replay marker*");
    }

    [Fact]
    public async Task NonSnapshotableBody_DoesNotBreakRoute_DegradesGracefully()
    {
        var reached = false;
        _ctx.AddRoutes(r => r
            .From("direct:poco").RouteId("poco")
            .Replayable("m")
            .Process(_ => reached = true));

        await _ctx.Start();
        // a plain POCO body is not snapshot-able → capture is skipped, route still runs
        await Send("direct:poco", new { Name = "x" });

        reached.Should().BeTrue();
    }

    [Fact]
    public async Task ExposedMarker_IsAddressableAsDirectEndpoint()
    {
        var runs = 0;
        _ctx.AddRoutes(r => r
            .From("direct:exp").RouteId("exp")
            .Process(_ => { })
            .Replayable("mk", exposed: true)
            .Process(_ => runs++));

        await _ctx.Start();
        await Send(RouteCheckpoint.EndpointUri("exp", "mk"), "payload");

        runs.Should().Be(1);
    }

    [Fact]
    public async Task ExposedMarker_ReachableViaTo_FromAnotherRoute()
    {
        var runs = 0;
        _ctx.AddRoutes(r => r
            .From("direct:host").RouteId("host")
            .Replayable("mk", exposed: true)
            .Process(_ => runs++));
        _ctx.AddRoutes(r => r
            .From("direct:caller").RouteId("caller")
            .To(RouteCheckpoint.EndpointUri("host", "mk")));

        await _ctx.Start();
        await Send("direct:caller", "x");

        runs.Should().Be(1);
    }

    [Fact]
    public async Task NonExposedMarker_IsNotAddressable()
    {
        _ctx.AddRoutes(r => r
            .From("direct:intl").RouteId("intl")
            .Replayable("mk")            // default: internal only
            .Process(_ => { }));
        await _ctx.Start();

        var act = () => Send(RouteCheckpoint.EndpointUri("intl", "mk"), "x");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*No consumer registered*");
    }

    private sealed class TrackedConnection(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    [Fact]
    public async Task Replay_DisposesScopedConnectionsTheTailCreated_NoLeak()
    {
        // The tail resolves its own scoped SQL/redb service (a fresh connection) from the context
        // provider and caches its scope on the exchange (as GetRedbService does under __redb_scope:*).
        // Replaying via ReplayAsync must dispose that scope — else every replay leaks a connection.
        var disposals = 0;
        var services = new ServiceCollection();
        services.AddScoped(_ => new TrackedConnection(() => Interlocked.Increment(ref disposals)));
        await using var provider = services.BuildServiceProvider();

        await using var ctx = new RouteContext(provider, "leak-test");
        ctx.AddComponent(new DirectComponent());

        var factory = provider.GetRequiredService<IServiceScopeFactory>();
        ctx.AddRoutes(r => r
            .From("direct:leak").RouteId("leak")
            .Replayable("m")
            .Process(ex =>
            {
                var scope = factory.CreateScope();
                _ = scope.ServiceProvider.GetRequiredService<TrackedConnection>();   // "open a connection"
                ex.Properties["__redb_scope:test"] = scope;                          // cached as the redb ext does
            }));

        await ctx.Start();

        await ctx.ReplayAsync("leak", "m", new Exchange(new Message("x")));

        disposals.Should().Be(1, "the scoped connection the tail created must be released after replay");
    }

    [Fact]
    public async Task NoMarkers_NoCheckpointCaptured()
    {
        IExchange? seen = null;
        _ctx.AddRoutes(r => r.From("direct:plain").RouteId("plain").Process(ex => seen = ex));
        await _ctx.Start();
        await Send("direct:plain", "hi");

        seen!.Properties.ContainsKey(RouteCheckpoint.PropertyKey).Should().BeFalse();
        _ctx.GetReplayMarkers().Should().BeEmpty();
    }
}
