using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Validation;

namespace redb.Route.Tests.Dsl;

/// <summary>
/// Closes the last two seams of the unification on live routes: <c>Validate</c> awaits its
/// predicate like every other branching scope, and an explicit expression in the value position
/// reads an operator without caring about the whitespace around it.
/// </summary>
[Collection("ExpressionResolver")]
public class ValueAndValidateEndToEndTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>A predicate that is only ever right asynchronously.</summary>
    private sealed class AsyncOnlyPredicate(Func<IExchange, bool> decide) : IPredicate
    {
        public int AsyncCalls { get; private set; }

        public bool Matches(IExchange exchange)
            => throw new InvalidOperationException("The engine must await MatchesAsync, not call Matches.");

        public async Task<bool> MatchesAsync(IExchange exchange)
        {
            await Task.Yield();
            AsyncCalls++;
            return decide(exchange);
        }
    }

    private async Task<IExchange> SendAmount(string uri, int amount)
    {
        var exchange = new Exchange(new Message("payload"));
        exchange.In.Headers["amount"] = amount;
        var producer = _context.GetEndpoint(uri).CreateProducer();
        await producer.Start();
        await producer.Process(exchange);
        return exchange;
    }

    // ── Validate awaits the predicate ─────────────────────────────────────────

    [Fact]
    public async Task Validate_AwaitsThePredicate_AndPassesWhenItHolds()
    {
        var predicate = new AsyncOnlyPredicate(e => (int)e.In.Headers["amount"]! > 0);
        var reached = false;
        _context.AddRoutes(r => r.From("direct://validate-async-ok")
            .Validate(predicate, "amount must be positive")
            .Process(_ => reached = true));
        await _context.Start();

        await SendAmount("direct://validate-async-ok", 5);

        reached.Should().BeTrue();
        predicate.AsyncCalls.Should().Be(1);
    }

    [Fact]
    public async Task Validate_AwaitsThePredicate_AndThrowsWhenItDoesNot()
    {
        var predicate = new AsyncOnlyPredicate(e => (int)e.In.Headers["amount"]! > 0);
        _context.AddRoutes(r => r.From("direct://validate-async-throw")
            .Validate(predicate, "amount must be positive")
            .Process(_ => { }));
        await _context.Start();

        var send = async () => await SendAmount("direct://validate-async-throw", -1);

        (await send.Should().ThrowAsync<ValidationException>()).WithMessage("*amount must be positive*");
        predicate.AsyncCalls.Should().Be(1);
    }

    [Fact]
    public async Task Validate_FromConditionString_StillWorksThroughTheSamePath()
    {
        var exchange = default(IExchange);
        _context.AddRoutes(r => r.From("direct://validate-string-soft")
            .Validate("header.amount>0", "amount must be positive", throwOnFailure: false)
            .Process(e => exchange = e));
        await _context.Start();

        await SendAmount("direct://validate-string-soft", -1);

        exchange!.Properties[ValidateProcessor.ValidationResultProperty].Should().Be(false);
    }

    // ── Value position: whitespace is not part of the language ────────────────

    [Theory]
    [InlineData("header.amount>1000")]
    [InlineData("header.amount > 1000")]
    [InlineData("header.amount\t>\t1000")]
    [InlineData("header.amount>1000 AND header.amount<9000")]
    public async Task SetHeader_WithExplicitExpression_ReadsTheOperatorWhateverTheSpacing(string expression)
    {
        var uri = "direct://value-expr-" + Guid.NewGuid().ToString("N");
        _context.AddRoutes(r => r.From(uri).SetHeader("big", new StringExpression(expression)).Process(_ => { }));
        await _context.Start();

        (await SendAmount(uri, 5000)).In.Headers["big"].Should().Be(true);
        (await SendAmount(uri, 10)).In.Headers["big"].Should().Be(false);
    }

    /// <summary>
    /// The other half of the same rule: a plain string in the value position is a literal,
    /// whatever operator-looking characters it carries. Only an explicit expression is parsed.
    /// </summary>
    [Theory]
    [InlineData("a>b")]
    [InlineData("black and white")]
    [InlineData("dGVzdC1zZWNyZXQ==")]
    public async Task SetHeader_WithPlainString_StaysALiteral(string literal)
    {
        var uri = "direct://value-literal-" + Guid.NewGuid().ToString("N");
        _context.AddRoutes(r => r.From(uri).SetHeader("v", literal).Process(_ => { }));
        await _context.Start();

        (await SendAmount(uri, 1)).In.Headers["v"].Should().Be(literal);
    }
}
