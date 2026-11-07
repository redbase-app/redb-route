using redb.Route.Abstractions;
using redb.Route.Aggregation;
using redb.Route.Core;
using redb.Route.Extensions;

namespace redb.Route.Tests.Core;

/// <summary>
/// The bodies an EIP runs on a timer, after the exchange that fed it has left — a debounce flush, an aggregation group
/// completed by its timeout, a resequencer batch released by its timer — run outside the route's error wrappers, as a
/// detached <c>.Threads()</c> body does. They are wrapped by the route's error scope, so a failure there reaches the
/// route's <c>OnException</c> (and the context's handlers), as Apache Camel's error handler covers an aggregation
/// completed by timeout. A completion on the arriving exchange stays inside the wrappers and is handled once.
/// </summary>
public class DetachedErrorScopeTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Debounce_FlushFailure_ReachesTheRoutesOnException()
    {
        var hits = 0;
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r => r
            .From("direct://detached-debounce")
            .OnException<InvalidOperationException>().Handled().Process(_ => Interlocked.Increment(ref hits)).End()
            .Debounce(_ => "k", TimeSpan.FromMilliseconds(20))
                .Process(_ => throw new InvalidOperationException("boom"))
            .End());
        await ctx.Start();

        await Send(ctx, "direct://detached-debounce");

        await WaitFor(() => Volatile.Read(ref hits) == 1);
    }

    [Fact]
    public async Task Aggregate_TimeoutCompletionFailure_ReachesTheRoutesOnException()
    {
        var hits = 0;
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r => r
            .From("direct://detached-aggregate")
            .OnException<InvalidOperationException>().Handled().Process(_ => Interlocked.Increment(ref hits)).End()
            .Aggregate("header.group", AggregationStrategies.UseLatest(), completionTimeout: TimeSpan.FromMilliseconds(50))
                .Process(_ => throw new InvalidOperationException("boom"))
            .End());
        await ctx.Start();

        await Send(ctx, "direct://detached-aggregate", ("group", "g1"));

        await WaitFor(() => Volatile.Read(ref hits) == 1);
    }

    [Fact]
    public async Task Aggregate_CompletionOnTheArrivingExchange_IsHandledOnce()
    {
        var hits = 0;
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r => r
            .From("direct://detached-aggregate-inline")
            .OnException<InvalidOperationException>().Handled().Process(_ => Interlocked.Increment(ref hits)).End()
            .Aggregate("header.group", AggregationStrategies.UseLatest(), completionSize: 1)
                .Process(_ => throw new InvalidOperationException("boom"))
            .End());
        await ctx.Start();

        await Send(ctx, "direct://detached-aggregate-inline", ("group", "g1"));
        await Task.Delay(200);

        Volatile.Read(ref hits).Should().Be(1, "completed on the caller, inside the route's wrappers — not again detached");
    }

    [Fact]
    public async Task Resequence_TimerReleaseFailure_ReachesTheRoutesOnException()
    {
        var hits = 0;
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r => r
            .From("direct://detached-resequence")
            .OnException<InvalidOperationException>().Handled().Process(_ => Interlocked.Increment(ref hits)).End()
            .Resequence(_ => 1, batchSize: 10, timeout: TimeSpan.FromMilliseconds(50))
                .Process(_ => throw new InvalidOperationException("boom"))
            .End());
        await ctx.Start();

        await Send(ctx, "direct://detached-resequence");

        await WaitFor(() => Volatile.Read(ref hits) == 1);
    }

    private static async Task Send(IRouteContext ctx, string uri, params (string Name, object? Value)[] headers)
    {
        var producer = ctx.GetEndpoint(uri).CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message { Body = "x" });
        foreach (var (name, value) in headers)
            exchange.In.Headers[name] = value;
        try { await producer.Process(exchange); }
        catch (Exception ex) { exchange.Exception ??= ex; }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(Wait);
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
    }
}
