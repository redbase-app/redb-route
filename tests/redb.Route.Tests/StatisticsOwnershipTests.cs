using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests;

/// <summary>
/// The statistics-ownership audit (plan KAFKA_HARDENING_AND_OPTIONS_SWEEP_PLAN, owner question 0).
/// One owner for pipeline statistics: the core. StatisticsProcessor wraps every From(), ToProcessor
/// counts every routed To(), and the ProducerTemplate counts its sends the same way - it used to
/// bypass statistics entirely, so a template send was invisible unless the connector
/// self-recorded, and a self-recording connector then double-counted in routes.
/// </summary>
public sealed class StatisticsOwnershipTests
{
    [Fact]
    public async Task TemplateSend_CountsMessagesOut_OnTheEndpoint()
    {
        await using var context = new RouteContext();
        await context.Start();

        // seda: buffers without a consumer, and no route touches the endpoint - so the ONLY
        // possible writer of its statistics is the template itself.
        using var template = new ProducerTemplate(context);
        template.Start();
        await template.SendAsync("seda:solo", "one");

        ((IEndpointStatistics)context.GetEndpoint("seda:solo")).MessagesOut.Should().Be(1,
            "template-otpravka must be counted like a routed .To()");
    }

    [Fact]
    public async Task TemplateSend_Failure_CountsBothLegs_OfAnInMemoryBridge()
    {
        await using var context = new RouteContext();
        context.AddRoutes(r => r.From("direct:boom").Process(_ => throw new InvalidOperationException("no")));
        await context.Start();

        using var template = new ProducerTemplate(context);
        template.Start();
        Func<Task> act = () => template.SendAsync("direct:boom", "one");
        await act.Should().ThrowAsync<InvalidOperationException>();

        // direct: is one endpoint object playing both roles, and each leg records ITS error:
        // the send leg (CountedSend) and the pipeline leg (StatisticsProcessor). Exactly two -
        // a third writer would mean a self-recording regression.
        ((IEndpointStatistics)context.GetEndpoint("direct:boom")).Errors.Should().Be(2,
            "ровно две ноги: ошибка отправки + ошибка пайплайна, третьего писателя быть не должно");
    }

    [Fact]
    public async Task RoutedToAnInMemoryBridge_CountsMessagesOutOnce()
    {
        // direct:mid is a producer target for the first route AND the From() of the second -
        // the same endpoint object. MessagesOut is producer-side only: ToProcessor writes 1,
        // and the consumer wrapper must not add its own on pipeline success.
        await using var context = new RouteContext();
        context.AddRoutes(r =>
        {
            r.From("direct:in").To("direct:mid");
            r.From("direct:mid").To("mock:out");
        });
        await context.Start();

        using var template = new ProducerTemplate(context);
        template.Start();
        await template.SendAsync("direct:in", "one");

        var mid = (IEndpointStatistics)context.GetEndpoint("direct:mid");
        mid.MessagesOut.Should().Be(1, "MessagesOut пишет только продюсерская сторона");
        mid.MessagesIn.Should().Be(1, "MessagesIn пишет только консьюмерская обёртка");
    }

    [Fact]
    public async Task RecipientListSend_CountsMessagesOut_LikeARoutedTo()
    {
        // The sending EIPs drive a producer directly (no ToProcessor). Their sends must land in
        // the same producer-side counters - they used to bypass statistics entirely.
        await using var context = new RouteContext();
        context.AddRoutes(r => r.From("direct:rl").RecipientList(_ => new[] { "seda:rl-target" }));
        await context.Start();

        using var template = new ProducerTemplate(context);
        template.Start();
        await template.SendAsync("direct:rl", "one");

        ((IEndpointStatistics)context.GetEndpoint("seda:rl-target")).MessagesOut.Should().Be(1,
            "отправка через RecipientList считается как маршрутный .To()");
    }

    [Fact]
    public async Task EnrichSend_CountsMessagesOut_LikeARoutedTo()
    {
        await using var context = new RouteContext();
        context.AddRoutes(r => r.From("direct:enr").Enrich("mock:enr-svc").To("mock:enr-out"));
        await context.Start();

        using var template = new ProducerTemplate(context);
        template.Start();
        await template.SendAsync("direct:enr", "one");

        ((IEndpointStatistics)context.GetEndpoint("mock:enr-svc")).MessagesOut.Should().Be(1,
            "запрос обогащения — тоже отправка через продюсера");
    }
}
