using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.ControlBus;
using redb.Route.Core;

namespace redb.Route.Tests.ControlBus;

/// <summary>
/// Tests for the Control Bus EIP (Apache Camel <c>controlbus:</c> parity): route actions
/// (start/stop/suspend/resume/restart/status/stats/fail), the <c>.ControlBus(...)</c> DSL,
/// <c>current</c> targeting, options validation, and producer-only enforcement.
/// </summary>
public class ControlBusTests
{
    private static async Task<IProducer> StartAndProducer(RouteContext ctx, string fromUri)
    {
        await ctx.Start();
        var producer = ctx.GetEndpoint(fromUri).CreateProducer();
        await producer.Start();
        return producer;
    }

    [Fact]
    public async Task ControlBus_OneRoute_StopsAnother()
    {
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r =>
        {
            r.From("direct://worker").RouteId("worker").Process(_ => { });
            r.From("direct://control").RouteId("controller")
                .ControlBus(ControlBusAction.Stop, "worker");
        });

        var control = await StartAndProducer(ctx, "direct://control");
        ctx.GetRoute("worker")!.Status.Should().Be(RouteStatus.Started);

        await control.Process(new Exchange(new Message("go")));

        ctx.GetRoute("worker")!.Status.Should().Be(RouteStatus.Stopped);
    }

    [Fact]
    public async Task ControlBus_Start_RevivesStoppedRoute()
    {
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r =>
        {
            r.From("direct://w2").RouteId("w2").Process(_ => { });
            r.From("direct://ctl-start").RouteId("c2")
                .ControlBus(ControlBusAction.Start, "w2");
        });

        await ctx.Start();
        await ctx.StopRoute("w2");
        ctx.GetRoute("w2")!.Status.Should().Be(RouteStatus.Stopped);

        var control = ctx.GetEndpoint("direct://ctl-start").CreateProducer();
        await control.Start();
        await control.Process(new Exchange(new Message("go")));

        ctx.GetRoute("w2")!.Status.Should().Be(RouteStatus.Started);
    }

    [Fact]
    public async Task ControlBus_Status_ReturnsStatusOnBody()
    {
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r =>
        {
            r.From("direct://w3").RouteId("w3").Process(_ => { });
            r.From("direct://ctl-status").RouteId("c3")
                .To("controlbus:route?routeId=w3&action=status");
        });

        var control = await StartAndProducer(ctx, "direct://ctl-status");
        var ex = new Exchange(new Message("x"));
        await control.Process(ex);

        ex.In.Body.Should().Be("Started");
    }

    [Fact]
    public async Task ControlBus_Stats_ReturnsXml()
    {
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r =>
        {
            r.From("direct://w4").RouteId("w4").Process(_ => { });
            r.From("direct://ctl-stats").RouteId("c4")
                .To("controlbus:route?routeId=w4&action=stats");
        });

        var control = await StartAndProducer(ctx, "direct://ctl-stats");
        var ex = new Exchange(new Message("x"));
        await control.Process(ex);

        var body = ex.In.Body!.ToString()!;
        body.Should().Contain("<routeStats>").And.Contain("id=\"w4\"").And.Contain("status=\"Started\"");
    }

    [Fact]
    public async Task ControlBus_Dsl_Current_StopsSelf_Async_NoDeadlock()
    {
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r =>
            r.From("direct://selfstop").RouteId("selfstop")
                .ControlBus(ControlBusAction.Suspend, "current", async: true));

        var producer = await StartAndProducer(ctx, "direct://selfstop");

        // Must return promptly (async dispatch), not deadlock stopping the current route.
        await producer.Process(new Exchange(new Message("x"))).WaitAsync(TimeSpan.FromSeconds(5));

        // Eventually the self-stop lands.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (ctx.GetRoute("selfstop")!.Status != RouteStatus.Stopped && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        ctx.GetRoute("selfstop")!.Status.Should().Be(RouteStatus.Stopped);
    }

    [Fact]
    public async Task ControlBus_MissingAction_FailsAtEndpointCreation()
    {
        await using var ctx = new RouteContext();
        var act = () => ctx.GetEndpoint("controlbus:route?routeId=x");
        act.Should().Throw<ArgumentException>().WithMessage("*action*");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ControlBus_MissingRouteId_FailsForMutatingAction()
    {
        await using var ctx = new RouteContext();
        var act = () => ctx.GetEndpoint("controlbus:route?action=stop");
        act.Should().Throw<ArgumentException>().WithMessage("*routeId*");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ControlBus_IsProducerOnly()
    {
        await using var ctx = new RouteContext();
        var endpoint = ctx.GetEndpoint("controlbus:route?routeId=x&action=stop");
        var act = () => endpoint.CreateConsumer(new NoopProcessor());
        act.Should().Throw<NotSupportedException>();
        await Task.CompletedTask;
    }

    private sealed class NoopProcessor : IProcessor
    {
        public Task Process(IExchange exchange, CancellationToken ct = default) => Task.CompletedTask;
    }
}
