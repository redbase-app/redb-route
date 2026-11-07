using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;
using redb.Route.TestKit;

namespace redb.Route.Tests.Core;

/// <summary>
/// The scope of what a <see cref="RouteBuilder"/> declares for its routes — <c>Intercept*</c>, <c>OnCompletion</c>,
/// <c>OnException</c>: the routes of that builder, never the routes of another builder of the same context
/// (Apache Camel's RouteBuilder scope). Also the detached path (<c>.Threads()</c>), which reaches the handlers through
/// <see cref="IRouteContext.HandleException"/> rather than through the route's wrapper, and the user's own
/// registrations, which a restart must not lose.
/// </summary>
public class BuilderScopeTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Intercept_OfOneBuilder_DoesNotReachTheRoutesOfAnother()
    {
        await using var ctx = new RouteContext();
        ctx.AddRoutes(a =>
        {
            a.Intercept().SetHeader("seenByA", true);
            a.From("direct://scope-a").To("mock://scope-a");
        });
        ctx.AddRoutes(b => b.From("direct://scope-b").To("mock://scope-b"));
        await ctx.Start();

        await ctx.SendBody("direct://scope-a", "a");
        await ctx.SendBody("direct://scope-b", "b");

        await ctx.Mock("mock://scope-a").ExpectHeader("seenByA", true).AssertIsSatisfiedAsync(Wait);
        ctx.Mock("mock://scope-b").ReceivedExchanges.Should().ContainSingle()
            .Which.In.Headers.Should().NotContainKey("seenByA", "an intercept is scoped to the builder that declares it");
    }

    [Fact]
    public async Task OnCompletion_OfOneBuilder_DoesNotRunForTheRoutesOfAnother()
    {
        var completions = 0;
        await using var ctx = new RouteContext();
        ctx.AddRoutes(a =>
        {
            a.OnCompletion().ModeBeforeConsumer().Process(_ => Interlocked.Increment(ref completions));
            a.From("direct://completion-a").To("mock://completion-a");
        });
        ctx.AddRoutes(b => b.From("direct://completion-b").To("mock://completion-b"));
        await ctx.Start();

        await ctx.SendBody("direct://completion-b", "b");
        await ctx.SendBody("direct://completion-a", "a");

        Volatile.Read(ref completions).Should().Be(1, "only the route of the declaring builder completes into it");
    }

    [Fact]
    public async Task OnException_OfOneBuilder_DoesNotHandleTheFailuresOfAnother()
    {
        var hitsA = 0;
        await using var ctx = new RouteContext();
        ctx.AddRoutes(a =>
        {
            a.OnException<InvalidOperationException>().Handled().Process(_ => Interlocked.Increment(ref hitsA));
            a.From("direct://failure-a").To("mock://failure-a");
        });
        ctx.AddRoutes(b => b.From("direct://failure-b").Process(_ => throw new InvalidOperationException("boom")));
        await ctx.Start();

        var exchange = await Send(ctx, "direct://failure-b");

        Volatile.Read(ref hitsA).Should().Be(0, "an OnException is scoped to the builder that declares it");
        exchange.Exception.Should().BeOfType<InvalidOperationException>("nothing in the failing route's scope handled it");
    }

    [Fact]
    public async Task TwoBuilders_WithTheSameOnExceptionType_Start()
    {
        await using var ctx = new RouteContext();
        ctx.AddRoutes(a =>
        {
            a.OnException<InvalidOperationException>().Handled();
            a.From("direct://same-type-a").To("mock://same-type-a");
        });
        ctx.AddRoutes(b =>
        {
            b.OnException<InvalidOperationException>().Handled();
            b.From("direct://same-type-b").To("mock://same-type-b");
        });

        var start = async () => await ctx.Start();

        await start.Should().NotThrowAsync("each builder's handler belongs to its own routes, they do not collide");
    }

    [Fact]
    public async Task Threads_RouteOnException_ReceivesTheDetachedFailure()
    {
        var hits = 0;
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r => r
            .From("direct://threads-route-handler")
            .OnException<InvalidOperationException>()
                .Handled()
                .Process(_ => Interlocked.Increment(ref hits))
            .End()
            .Threads(2)
                .Process((IExchange _, CancellationToken _) => throw new InvalidOperationException("boom"))
            .End());
        await ctx.Start();

        await Send(ctx, "direct://threads-route-handler");

        await WaitFor(() => Volatile.Read(ref hits) == 1);
    }

    [Fact]
    public async Task Threads_DetachedFailure_ReachesOnlyTheHandlersOfItsOwnBuilder()
    {
        var hitsA = 0;
        var hitsB = 0;
        await using var ctx = new RouteContext();
        ctx.AddRoutes(a =>
        {
            a.OnException<InvalidOperationException>().Handled().Process(_ => Interlocked.Increment(ref hitsA));
            a.From("direct://threads-scope-a").To("mock://threads-scope-a");
        });
        ctx.AddRoutes(b =>
        {
            b.OnException<Exception>().Handled().Process(_ => Interlocked.Increment(ref hitsB));
            b.From("direct://threads-scope-b")
                .Threads(2)
                    .Process((IExchange _, CancellationToken _) => throw new InvalidOperationException("boom"))
                .End();
        });
        await ctx.Start();

        await Send(ctx, "direct://threads-scope-b");

        await WaitFor(() => Volatile.Read(ref hitsA) + Volatile.Read(ref hitsB) >= 1);
        Volatile.Read(ref hitsB).Should().Be(1);
        Volatile.Read(ref hitsA).Should().Be(0, "a more specific handler of another builder is out of scope");
    }

    [Fact]
    public async Task Threads_RouteOnException_TakesPrecedenceOverTheBuilders()
    {
        var routeHits = 0;
        var builderHits = 0;
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r =>
        {
            r.OnException<InvalidOperationException>().Handled().Process(_ => Interlocked.Increment(ref builderHits));
            r.From("direct://threads-precedence")
                .OnException<InvalidOperationException>()
                    .Handled()
                    .Process(_ => Interlocked.Increment(ref routeHits))
                .End()
                .Threads(2)
                    .Process((IExchange _, CancellationToken _) => throw new InvalidOperationException("boom"))
                .End();
        });
        await ctx.Start();

        await Send(ctx, "direct://threads-precedence");

        await WaitFor(() => Volatile.Read(ref routeHits) + Volatile.Read(ref builderHits) >= 1);
        Volatile.Read(ref routeHits).Should().Be(1);
        Volatile.Read(ref builderHits).Should().Be(0, "the route's own OnException wins, as it does on the inline path");
    }

    [Fact]
    public async Task Restart_KeepsTheGlobalHandlerTheUserRegistered()
    {
        var hits = 0;
        await using var ctx = new RouteContext();
        ctx.AddGlobalExceptionHandler<InvalidOperationException>(
            new DelegateProcessor(_ => Interlocked.Increment(ref hits)));
        ctx.AddRoutes(r => r
            .From("direct://restart-global")
            .Threads(2)
                .Process((IExchange _, CancellationToken _) => throw new InvalidOperationException("boom"))
            .End());

        await ctx.Start();
        await Send(ctx, "direct://restart-global");
        await WaitFor(() => Volatile.Read(ref hits) == 1);

        await ctx.Stop();
        await ctx.Start();
        await Send(ctx, "direct://restart-global");

        await WaitFor(() => Volatile.Read(ref hits) == 2);
    }

    [Fact]
    public async Task Restart_KeepsTheLocalHandlerTheUserRegistered()
    {
        var hits = 0;
        await using var ctx = new RouteContext();
        ctx.AddLocalExceptionHandler("restart-local", typeof(InvalidOperationException),
            new DelegateProcessor(_ => Interlocked.Increment(ref hits)));
        ctx.AddRoutes(r => r
            .From("direct://restart-local")
            .RouteId("restart-local")
            .Threads(2)
                .Process((IExchange _, CancellationToken _) => throw new InvalidOperationException("boom"))
            .End());

        await ctx.Start();
        await Send(ctx, "direct://restart-local");
        await WaitFor(() => Volatile.Read(ref hits) == 1);

        await ctx.Stop();
        await ctx.Start();
        await Send(ctx, "direct://restart-local");

        await WaitFor(() => Volatile.Read(ref hits) == 2);
    }

    private static async Task<IExchange> Send(IRouteContext ctx, string uri)
    {
        var producer = ctx.GetEndpoint(uri).CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message { Body = "x" });
        try { await producer.Process(exchange); }
        catch (Exception ex) { exchange.Exception ??= ex; }
        return exchange;
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
