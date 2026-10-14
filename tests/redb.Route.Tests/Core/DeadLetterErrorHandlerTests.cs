using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.ErrorHandling;
using redb.Route.TestKit;

namespace redb.Route.Tests.Core;

/// <summary>
/// The context's dead letter channel — Apache Camel's context-level <c>errorHandler(deadLetterChannel(...))</c>: every
/// route sends to it a failure no <c>OnException</c> took, stamped with the <c>CamelDeadLetter*</c> headers, and the
/// failure is handled; a failure an <c>OnException</c> took never reaches it; a lost dead letter lets the failure go on.
/// </summary>
public class DeadLetterErrorHandlerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task AFailureNoOnExceptionTook_GoesToTheDeadLetterRoute_AndIsHandled()
    {
        await using var ctx = new RouteContext();
        ctx.ErrorHandler = new DeadLetterErrorHandler(ctx, "mock://dlc-errors");
        ctx.AddRoutes(r => r.From("direct://dlc-orders").Process(_ => throw new InvalidOperationException("boom")));
        await ctx.Start();

        var exchange = await Send(ctx, "direct://dlc-orders");

        exchange.ExceptionHandled.Should().BeTrue("a dead letter channel takes the failure, as in Camel");
        var dead = ctx.Mock("mock://dlc-errors");
        await dead.ExpectBodies("x").AssertIsSatisfiedAsync(Wait);
        var headers = dead.ReceivedExchanges.Single().In.Headers;
        headers["CamelDeadLetterReason"].Should().Be("boom");
        headers["CamelDeadLetterExceptionType"].Should().Be(typeof(InvalidOperationException).FullName);
    }

    [Fact]
    public async Task AFailureAnOnExceptionTook_NeverReachesIt()
    {
        await using var ctx = new RouteContext();
        ctx.ErrorHandler = new DeadLetterErrorHandler(ctx, "mock://dlc-taken-errors");
        ctx.AddRoutes(r => r
            .From("direct://dlc-taken")
            .OnException<InvalidOperationException>().Handled().End()
            .Process(_ => throw new InvalidOperationException("boom")));
        await ctx.Start();

        await Send(ctx, "direct://dlc-taken");

        ctx.Mock("mock://dlc-taken-errors").ReceivedExchanges.Should().BeEmpty();
    }

    [Fact]
    public async Task ALostDeadLetter_LetsTheFailureGoOn()
    {
        await using var ctx = new RouteContext();
        ctx.ErrorHandler = new DeadLetterErrorHandler(ctx, "direct://dlc-nobody-listens");
        ctx.AddRoutes(r => r.From("direct://dlc-lost").Process(_ => throw new InvalidOperationException("boom")));
        await ctx.Start();

        var exchange = await Send(ctx, "direct://dlc-lost");

        exchange.ExceptionHandled.Should().BeFalse("the dead letter was not delivered, so the failure is not swallowed");
        exchange.Exception.Should().BeOfType<InvalidOperationException>();
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
}
