using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;

namespace redb.Route.Tests.Dsl;

/// <summary>
/// End-to-end coverage of an <see cref="IExpression"/> written in a condition position. The
/// string-born expression — <c>Expr("...")</c>, i.e. <see cref="StringExpression"/> — is the
/// case that mattered: until 2026-08-28 it was read as a value and then coerced, so
/// <c>Filter(Expr("header.amount>1000"))</c> looked up a header literally named
/// <c>amount&gt;1000</c> and silently dropped every message. The string overload had already
/// been fixed; this closes the same hole for the expression overload.
/// </summary>
[Collection("ExpressionResolver")]
public class ExpressionConditionEndToEndTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private async Task<bool> SendAmount(string uri, int amount)
    {
        var exchange = new Exchange(new Message("payload"));
        exchange.In.Headers["amount"] = amount;
        var producer = _context.GetEndpoint(uri).CreateProducer();
        await producer.Start();
        await producer.Process(exchange);
        return exchange.Properties.TryGetValue("reached", out var reached) && reached is true;
    }

    [Theory]
    [InlineData("header.amount>1000")]
    [InlineData("header.amount > 1000")]
    [InlineData("header.amount\t>\t1000")]
    public async Task Filter_WithStringBornExpression_ComparesLikeTheStringOverload(string condition)
    {
        var uri = "direct://expr-filter-" + Guid.NewGuid().ToString("N");
        _context.AddRoutes(r => r.From(uri)
            .Filter(new StringExpression(condition))
            .Process(e => e.Properties["reached"] = true));
        await _context.Start();

        (await SendAmount(uri, 5000)).Should().BeTrue("5000 > 1000");
        (await SendAmount(uri, 10)).Should().BeFalse("10 is not > 1000");
    }

    [Fact]
    public async Task Filter_WithStringBornExpression_AgreesWithTheStringOverloadOnEveryMessage()
    {
        var viaExpression = new List<int>();
        var viaString = new List<int>();
        _context.AddRoutes(r =>
        {
            r.From("direct://expr-a").Filter(new StringExpression("header.amount>1000"))
                .Process(e => viaExpression.Add((int)e.In.Headers["amount"]!));
            r.From("direct://expr-b").Filter("header.amount>1000")
                .Process(e => viaString.Add((int)e.In.Headers["amount"]!));
        });
        await _context.Start();

        foreach (var amount in new[] { 10, 1000, 1001, 5000 })
        {
            await SendAmount("direct://expr-a", amount);
            await SendAmount("direct://expr-b", amount);
        }

        viaExpression.Should().Equal(viaString);
        viaExpression.Should().Equal(1001, 5000);
    }

    [Fact]
    public async Task When_WithStringBornExpression_RoutesLikeTheStringOverload()
    {
        string? taken = null;
        _context.AddRoutes(r => r.From("direct://expr-when").Choice()
            .When(new StringExpression("header.amount>1000")).Process(_ => taken = "big").EndWhen()
            .Otherwise().Process(_ => taken = "small").EndChoice());
        await _context.Start();

        await SendAmount("direct://expr-when", 5000);
        taken.Should().Be("big");

        await SendAmount("direct://expr-when", 5);
        taken.Should().Be("small");
    }

    [Fact]
    public async Task Filter_WithStringBornTemplate_KeepsTheTruthinessReading()
    {
        var uri = "direct://expr-tpl";
        _context.AddRoutes(r => r.From(uri)
            .Filter(new StringExpression("${header.amount}"))
            .Process(e => e.Properties["reached"] = true));
        await _context.Start();

        (await SendAmount(uri, 7)).Should().BeTrue("a non-zero number is truthy");
        (await SendAmount(uri, 0)).Should().BeFalse("zero is false");
    }

    [Fact]
    public async Task Filter_WithMalformedStringBornExpression_FailsWhileTheRouteIsBuilt()
    {
        // The value dialect accepts "header.amount >" as a lookup of a header named "amount >",
        // so the StringExpression itself constructs; the condition position rejects it when the
        // route is compiled at Start(), before any message flows.
        var reached = false;
        _context.AddRoutes(r => r.From("direct://expr-broken")
            .Filter(new StringExpression("header.amount >"))
            .Process(_ => reached = true));

        var start = async () => await _context.Start();

        await start.Should().ThrowAsync<ExpressionCompilationException>();
        reached.Should().BeFalse();
    }

    /// <summary>
    /// A typed expression that is not string-born keeps the plain truthiness reading: it has no
    /// source text to compile as a condition, only a value to read.
    /// </summary>
    [Theory]
    [InlineData(5000, true)]
    [InlineData(0, false)]
    public async Task Filter_WithTypedExpression_ReadsTheValueForTruthiness(int amount, bool expected)
    {
        var uri = "direct://expr-typed-" + amount;
        _context.AddRoutes(r => r.From(uri)
            .Filter(new HeaderExpression("amount"))
            .Process(e => e.Properties["reached"] = true));
        await _context.Start();

        (await SendAmount(uri, amount)).Should().Be(expected);
    }
}
