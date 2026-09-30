using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// A body that needs disposing (a stream, a <see cref="StreamCache"/>) is disposed by the unit of work that owns it, once,
/// when that owner is done — as in Camel, where the original exchange's unit of work cleans up and a copy does not. A copy
/// shares the body of its origin by reference, and it used to dispose it: a fan-out step (RecipientList, Splitter,
/// Scatter-Gather, a copying Loop) closed the body the route went on with, the aggregated result included, and
/// <c>.Threads()</c> lost its body to the caller disposing the original while the worker still read it.
/// </summary>
public class ExchangeBodyOwnershipTests
{
    /// <summary>A stream that counts its disposals and refuses to be read after the first.</summary>
    private sealed class TrackedStream() : MemoryStream("payload"u8.ToArray())
    {
        public int Disposals;
        protected override void Dispose(bool disposing)
        {
            if (disposing) Interlocked.Increment(ref Disposals);
            base.Dispose(disposing);
        }
    }

    // ── The rule, on the exchange ──

    [Fact]
    public async Task A_copy_does_not_dispose_the_body_it_shares_with_its_origin()
    {
        var body = new TrackedStream();
        var root = new Exchange(new Message(body));
        var copy = root.Clone();

        await copy.DisposeAsync();
        body.Disposals.Should().Be(0, "the origin still carries it");

        await root.DisposeAsync();
        body.Disposals.Should().Be(1);
    }

    [Fact]
    public async Task A_body_a_copy_made_and_handed_to_its_origin_lives_as_long_as_the_origin()
    {
        var root = new Exchange(new Message("in"));
        var copy = root.Clone();
        var result = new TrackedStream();
        copy.In.Body = result;
        root.In.Body = copy.In.Body;   // what an aggregation does

        await copy.DisposeAsync();
        result.Disposals.Should().Be(0);

        await root.DisposeAsync();
        result.Disposals.Should().Be(1);
    }

    [Fact]
    public async Task A_body_the_origin_lent_and_then_replaced_is_still_disposed_by_the_origin()
    {
        var original = new TrackedStream();
        var root = new Exchange(new Message(original));
        var copy = root.Clone();
        root.In.Body = "aggregated";

        await copy.DisposeAsync();
        original.Disposals.Should().Be(0, "it is the origin's to dispose");
        await root.DisposeAsync();
        original.Disposals.Should().Be(1, "replacing it must not leak it");
    }

    [Fact]
    public async Task A_body_a_copy_made_and_kept_is_disposed_by_the_copy()
    {
        var root = new Exchange(new Message("in"));
        var part = new TrackedStream();
        var child = root.CreateChild(new Message(part));

        await child.DisposeAsync();

        part.Disposals.Should().Be(1);
        await root.DisposeAsync();
        part.Disposals.Should().Be(1);
    }

    [Fact]
    public async Task Disposing_an_exchange_twice_disposes_its_body_once()
    {
        var body = new TrackedStream();
        var root = new Exchange(new Message(body));

        await root.DisposeAsync();
        await root.DisposeAsync();

        body.Disposals.Should().Be(1);
    }

    [Fact]
    public async Task A_body_shared_by_In_and_Out_is_disposed_once()
    {
        var body = new TrackedStream();
        var root = new Exchange(new Message(body)) { Out = new Message(body) };

        await root.DisposeAsync();

        body.Disposals.Should().Be(1);
    }

    // ── The EIPs the review named ──

    private static async Task<RouteContext> Routes(Action<InlineRouteBuilder> build)
    {
        var context = new RouteContext($"body-ownership-{Guid.NewGuid():N}");
        context.AddComponent(new DirectComponent());
        context.AddRoutes(build);
        await context.Start();
        return context;
    }

    [Fact]
    public async Task RecipientList_keeps_open_the_aggregated_body_the_route_goes_on_with()
    {
        TrackedStream? produced = null;
        var disposalsAfterStep = -1;
        await using var context = await Routes(r =>
        {
            r.From("direct://rl-in")
                .RecipientList(_ => ["direct://rl-producer"], aggregationStrategy: static (_, latest) => latest)
                .Process(e => disposalsAfterStep = ((TrackedStream)e.In.Body!).Disposals);
            r.From("direct://rl-producer").Process(e => e.In.Body = produced = new TrackedStream());
        });
        var template = new ProducerTemplate(context);
        template.Start();

        await template.SendAsync("direct://rl-in", "in");
        template.Dispose();

        disposalsAfterStep.Should().Be(0, "the route goes on with the aggregated body");
        produced!.Disposals.Should().Be(1, "and it is disposed once, when the exchange is done");
    }

    [Fact]
    public async Task A_copying_Loop_keeps_open_the_body_merged_back()
    {
        var disposalsAfterLoop = -1;
        TrackedStream? last = null;
        await using var context = await Routes(r =>
            r.From("direct://loop-in")
                .Loop(2, copy: true)
                    .Process(e => e.In.Body = last = new TrackedStream())
                .EndLoop()
                .Process(e => disposalsAfterLoop = ((TrackedStream)e.In.Body!).Disposals));
        var template = new ProducerTemplate(context);
        template.Start();

        await template.SendAsync("direct://loop-in", "in");
        template.Dispose();

        disposalsAfterLoop.Should().Be(0);
        last!.Disposals.Should().Be(1);
    }

    [Fact]
    public async Task Threads_keeps_the_body_open_for_the_worker_after_the_caller_is_done()
    {
        var body = new TrackedStream();
        var read = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var context = await Routes(r =>
            r.From("direct://threads-in")
                .Threads(1)
                    .Process(async (e, _) =>
                    {
                        await Task.Delay(200);
                        read.TrySetResult(((TrackedStream)e.In.Body!).Disposals);
                    }));
        var template = new ProducerTemplate(context);
        template.Start();

        await template.SendAsync("direct://threads-in", body);   // the caller disposes its exchange right after
        template.Dispose();

        (await read.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(0, "the worker still reads the body");
        await context.Stop();
        body.Disposals.Should().Be(1, "the worker's exchange disposes it once");
    }
}
