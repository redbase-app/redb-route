using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// Which <c>OnException</c> takes a failure — Apache Camel's exception policy (<c>DefaultExceptionPolicyStrategy</c>):
/// the failure is walked from its innermost cause up to the thrown exception; a handler of exactly that type wins at
/// once; otherwise the handler whose type is the nearest ancestor does, wherever it is declared — on the route, on its
/// builder, in a route configuration; the route's own handler wins a tie; a later handler of the same type replaces the
/// earlier one; a handler whose <c>OnWhen</c> is false is passed over.
/// </summary>
public class ExceptionPolicyTests
{
    [Fact]
    public async Task TheNearestType_Wins_WhateverTheDeclarationOrder()
    {
        var taken = new List<string>();
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r =>
        {
            // The wide one first: it used to sit innermost and take everything.
            r.OnException<Exception>().Handled().Process(_ => Take(taken, "exception"));
            r.OnException<IOException>().Handled().Process(_ => Take(taken, "io"));
            r.OnException<FileNotFoundException>().Handled().Process(_ => Take(taken, "file"));
            r.From("direct://policy-nearest").Process(Throw);
        });
        await ctx.Start();

        await Send(ctx, "direct://policy-nearest", new FileNotFoundException("f"));
        await Send(ctx, "direct://policy-nearest", new EndOfStreamException("e"));
        await Send(ctx, "direct://policy-nearest", new InvalidOperationException("o"));

        taken.Should().Equal("file", "io", "exception");
    }

    [Fact]
    public async Task ABuildersNearerHandler_WinsOverTheRoutesWiderOne()
    {
        var taken = new List<string>();
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r =>
        {
            r.OnException<IOException>().Handled().Process(_ => Take(taken, "builder"));
            r.From("direct://policy-scopes")
                .OnException<Exception>().Handled().Process(_ => Take(taken, "route")).End()
                .Process(Throw);
        });
        await ctx.Start();

        await Send(ctx, "direct://policy-scopes", new IOException("io"));
        await Send(ctx, "direct://policy-scopes", new InvalidOperationException("op"));

        taken.Should().Equal("builder", "route");
    }

    [Fact]
    public async Task TheRoutesOwnHandler_WinsATieWithItsBuilders()
    {
        var taken = new List<string>();
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r =>
        {
            r.OnException<IOException>().Handled().Process(_ => Take(taken, "builder"));
            r.From("direct://policy-tie")
                .OnException<IOException>().Handled().Process(_ => Take(taken, "route")).End()
                .Process(Throw);
        });
        await ctx.Start();

        await Send(ctx, "direct://policy-tie", new IOException("io"));

        taken.Should().Equal("route");
    }

    [Fact]
    public async Task ACause_IsMatched()
    {
        var taken = new List<string>();
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r =>
        {
            r.OnException<IOException>().Handled().Process(e => Take(taken, e.Exception!.GetType().Name));
            r.From("direct://policy-cause").Process(Throw);
        });
        await ctx.Start();

        var exchange = await Send(ctx, "direct://policy-cause",
            new InvalidOperationException("wrapper", new IOException("the cause")));

        taken.Should().ContainSingle().Which.Should().Be("InvalidOperationException",
            "the handler gets the exception that was thrown, as in Camel");
        exchange.Exception.Should().BeNull();
    }

    [Fact]
    public async Task TheInnermostCause_IsLookedAtFirst()
    {
        var taken = new List<string>();
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r =>
        {
            r.OnException<InvalidOperationException>().Handled().Process(_ => Take(taken, "wrapper"));
            r.OnException<IOException>().Handled().Process(_ => Take(taken, "cause"));
            r.From("direct://policy-innermost").Process(Throw);
        });
        await ctx.Start();

        await Send(ctx, "direct://policy-innermost", new InvalidOperationException("wrapper", new IOException("cause")));

        taken.Should().Equal("cause");
    }

    [Fact]
    public async Task AHandlerWhoseOnWhenIsFalse_IsPassedOver()
    {
        var taken = new List<string>();
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r =>
        {
            r.OnException<IOException>().OnWhen(e => e.In.Headers.ContainsKey("urgent"))
                .Handled().Process(_ => Take(taken, "urgent"));
            r.OnException<Exception>().Handled().Process(_ => Take(taken, "any"));
            r.From("direct://policy-when").Process(Throw);
        });
        await ctx.Start();

        await Send(ctx, "direct://policy-when", new IOException("io"));
        await Send(ctx, "direct://policy-when", new IOException("io"), ("urgent", true));

        taken.Should().Equal("any", "urgent");
    }

    [Fact]
    public async Task ALaterHandlerOfTheSameType_ReplacesTheEarlier()
    {
        var taken = new List<string>();
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r =>
        {
            r.OnException<IOException>().Handled().Process(_ => Take(taken, "first"));
            r.OnException<IOException>().Handled().Process(_ => Take(taken, "second"));
            r.From("direct://policy-replace").Process(Throw);
        });
        await ctx.Start();

        await Send(ctx, "direct://policy-replace", new IOException("io"));

        taken.Should().Equal("second");
    }

    [Fact]
    public async Task ARedelivery_MayEndInAnotherHandler()
    {
        // One scope, one redelivery loop: the handler is chosen again for the failure of every attempt.
        var taken = new List<string>();
        var attempts = 0;
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r =>
        {
            r.OnException<IOException>().MaximumRedeliveries(1).RedeliveryDelay(TimeSpan.FromMilliseconds(1))
                .Handled().Process(_ => Take(taken, "io"));
            r.OnException<InvalidOperationException>().Handled().Process(_ => Take(taken, "invalid"));
            r.From("direct://policy-redelivery").Process(_ =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                    throw new IOException("first attempt");
                throw new InvalidOperationException("second attempt");
            });
        });
        await ctx.Start();

        await Send(ctx, "direct://policy-redelivery", "x");

        attempts.Should().Be(2);
        taken.Should().Equal("invalid");
    }

    private static void Take(List<string> taken, string who)
    {
        lock (taken)
            taken.Add(who);
    }

    private static void Throw(IExchange exchange) => throw (Exception)exchange.In.Body!;

    private static async Task<IExchange> Send(IRouteContext ctx, string uri, object body,
        params (string Name, object? Value)[] headers)
    {
        var producer = ctx.GetEndpoint(uri).CreateProducer();
        await producer.Start();
        var message = new Message { Body = body };
        foreach (var (name, value) in headers)
            message.Headers[name] = value;
        var exchange = new Exchange(message);
        try { await producer.Process(exchange); }
        catch (Exception ex) { exchange.Exception ??= ex; }
        return exchange;
    }
}
