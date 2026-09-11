using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Expressions;
using redb.Route.Validation;

namespace redb.Route.Tests.Dsl;

/// <summary>
/// Behaviour of the string-condition overloads of the route DSL: Filter, When, LoopWhile and
/// Validate. They all compile a condition the same way, so a condition means the same thing
/// wherever it is written.
/// </summary>
// Shares the process-wide ExpressionResolver caches with the other expression tests, so it runs
// in the same collection: cache-statistics assertions there are global and must not race.
[Collection("ExpressionResolver")]
public class StringConditionTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private async Task<IExchange> Send(string uri, IExchange exchange)
    {
        var producer = _context.GetEndpoint(uri).CreateProducer();
        await producer.Start();
        await producer.Process(exchange);
        return exchange;
    }

    private static IExchange WithAmount(int amount)
    {
        var exchange = new Exchange(new Message("payload"));
        exchange.In.Headers["amount"] = amount;
        return exchange;
    }

    // ── Filter ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("header.amount > 1000")]
    [InlineData("header.amount>1000")]
    public async Task Filter_ComparisonPassesTheMatchingMessage(string condition)
    {
        object? captured = null;
        var uri = "direct://filter-pass-" + condition.GetHashCode().ToString("x");
        _context.AddRoutes(r => r.From(uri).Filter(condition).Process(e => captured = e.In.Body));
        await _context.Start();

        await Send(uri, WithAmount(5000));

        captured.Should().Be("payload");
    }

    [Theory]
    [InlineData("header.amount > 1000")]
    [InlineData("header.amount>1000")]
    public async Task Filter_ComparisonBlocksTheNonMatchingMessage(string condition)
    {
        object? captured = null;
        var uri = "direct://filter-block-" + condition.GetHashCode().ToString("x");
        _context.AddRoutes(r => r.From(uri).Filter(condition).Process(e => captured = e.In.Body));
        await _context.Start();

        await Send(uri, WithAmount(10));

        captured.Should().BeNull();
    }

    /// <summary>
    /// The regression this whole change is about: without the fix the unspaced form compiles to a
    /// header lookup named <c>amount&gt;1000</c>, which is always missing, so the filter silently
    /// dropped every message including the ones it was supposed to pass.
    /// </summary>
    [Fact]
    public async Task Filter_UnspacedComparison_BehavesExactlyLikeTheSpacedForm()
    {
        var spaced = new List<int>();
        var unspaced = new List<int>();
        _context.AddRoutes(r =>
        {
            r.From("direct://filter-spaced")
                .Filter("header.amount > 1000").Process(e => spaced.Add((int)e.In.Headers["amount"]!));
            r.From("direct://filter-unspaced")
                .Filter("header.amount>1000").Process(e => unspaced.Add((int)e.In.Headers["amount"]!));
        });
        await _context.Start();

        foreach (var amount in new[] { 10, 1000, 1001, 5000 })
        {
            await Send("direct://filter-spaced", WithAmount(amount));
            await Send("direct://filter-unspaced", WithAmount(amount));
        }

        unspaced.Should().Equal(spaced);
        unspaced.Should().Equal(1001, 5000);
    }

    [Theory]
    [InlineData("true", "payload")]
    [InlineData("false", null)]
    public async Task Filter_WithTemplate_KeepsHistoricalBehaviour(string headerValue, string? expected)
    {
        object? captured = null;
        _context.AddRoutes(r => r.From("direct://filter-template-" + headerValue)
            .Filter("${header.enabled}").Process(e => captured = e.In.Body));
        await _context.Start();

        var exchange = new Exchange(new Message("payload"));
        exchange.In.Headers["enabled"] = headerValue;
        await Send("direct://filter-template-" + headerValue, exchange);

        captured.Should().Be(expected);
    }

    /// <summary>
    /// A condition that cannot be compiled stops the route from starting. Before the unification
    /// it compiled to a missing-header lookup and quietly turned into "never match" on live
    /// traffic, with nothing in the log.
    /// </summary>
    [Fact]
    public async Task Filter_WithMalformedCondition_ThrowsWhileTheRouteIsBuilt()
    {
        var reached = false;
        _context.AddRoutes(r => r.From("direct://filter-broken").Filter("header.amount >").Process(_ => reached = true));

        var start = async () => await _context.Start();

        await start.Should().ThrowAsync<ExpressionCompilationException>();
        reached.Should().BeFalse();
    }

    [Fact]
    public async Task Filter_WithBlankCondition_ThrowsWhileTheRouteIsBuilt()
    {
        _context.AddRoutes(r => r.From("direct://filter-blank").Filter("   ").Process(_ => { }));

        var start = async () => await _context.Start();

        await start.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Filter_RecordsTheConditionItWasGiven()
    {
        FilterDefinition? filter = null;
        _context.AddRoutes(r => filter = r.From("direct://filter-source").Filter("header.amount>1000"));
        await _context.Start();

        filter!.SourceTemplate.Should().Be("header.amount>1000");
        filter.SourcePredicate.Should().NotBeNull();
        filter.SourcePredicate!.Matches(WithAmount(5000)).Should().BeTrue();
        filter.SourcePredicate.Matches(WithAmount(5)).Should().BeFalse();
    }

    [Fact]
    public async Task Filter_NestedConfiguratorOverload_RecordsTheConditionToo()
    {
        FilterDefinition? filter = null;
        object? captured = null;
        _context.AddRoutes(r => r.From("direct://filter-nested")
            .Filter("header.amount>1000", f =>
            {
                filter = f;
                f.Process(e => captured = e.In.Body);
            }));
        await _context.Start();

        await Send("direct://filter-nested", WithAmount(5000));

        filter!.SourceTemplate.Should().Be("header.amount>1000");
        filter.SourcePredicate.Should().NotBeNull();
        captured.Should().Be("payload");
    }

    // ── When ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task When_UnspacedComparison_BehavesExactlyLikeTheSpacedForm()
    {
        var spaced = new List<string>();
        var unspaced = new List<string>();
        _context.AddRoutes(r =>
        {
            r.From("direct://when-spaced").Choice()
                .When("header.amount > 1000").Process(_ => spaced.Add("big")).EndWhen()
                .Otherwise().Process(_ => spaced.Add("small")).EndChoice();
            r.From("direct://when-unspaced").Choice()
                .When("header.amount>1000").Process(_ => unspaced.Add("big")).EndWhen()
                .Otherwise().Process(_ => unspaced.Add("small")).EndChoice();
        });
        await _context.Start();

        foreach (var amount in new[] { 10, 1000, 1001, 5000 })
        {
            await Send("direct://when-spaced", WithAmount(amount));
            await Send("direct://when-unspaced", WithAmount(amount));
        }

        unspaced.Should().Equal(spaced);
        unspaced.Should().Equal("small", "small", "big", "big");
    }

    [Fact]
    public async Task When_RecordsTheConditionItWasGiven()
    {
        WhenDefinition? branch = null;
        _context.AddRoutes(r => branch = r.From("direct://when-source").Choice().When("header.amount>1000"));
        await _context.Start();

        branch!.SourceTemplate.Should().Be("header.amount>1000");
        branch.SourceExpression.Should().BeNull("a condition written as a string is not an IExpression");
        branch.SourcePredicate.Should().NotBeNull();
        branch.SourcePredicate!.Matches(WithAmount(5000)).Should().BeTrue();
    }

    /// <summary>
    /// <c>When</c> reached through the route-level alias, which looks up the enclosing Choice.
    /// Before the unification this path recorded nothing at all.
    /// </summary>
    [Fact]
    public async Task AliasWhen_RecordsTheConditionItWasGiven()
    {
        WhenDefinition? branch = null;
        _context.AddRoutes(r =>
        {
            var choice = r.From("direct://when-alias").Choice();
            var nested = choice.When("header.amount>9000").Filter("header.amount>9500");
            // Reached from a scope inside the When: the alias walks up to the enclosing Choice.
            branch = nested.When("header.amount>1000");
        });
        await _context.Start();

        branch!.SourceTemplate.Should().Be("header.amount>1000");
        branch.SourceExpression.Should().BeNull("a condition written as a string is not an IExpression");
        branch.SourcePredicate.Should().NotBeNull();
        branch.SourcePredicate!.Matches(WithAmount(5000)).Should().BeTrue();
        branch.SourcePredicate.Matches(WithAmount(5)).Should().BeFalse();
    }

    // ── LoopWhile ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoopWhile_RepeatsWhileTheConditionHolds()
    {
        var iterations = 0;
        _context.AddRoutes(r => r.From("direct://loop-while")
            .LoopWhile("property.i<3")
            .Process(e =>
            {
                iterations++;
                e.Properties["i"] = (int)e.Properties["i"]! + 1;
            })
            .EndLoop());
        await _context.Start();

        var exchange = new Exchange(new Message("payload"));
        exchange.Properties["i"] = 0;
        await Send("direct://loop-while", exchange);

        iterations.Should().Be(3);
        exchange.Properties["i"].Should().Be(3);
    }

    [Fact]
    public async Task LoopWhile_NestedConfiguratorOverload_RepeatsWhileTheConditionHolds()
    {
        var iterations = 0;
        _context.AddRoutes(r => r.From("direct://loop-while-nested")
            .LoopWhile("property.i<2", loop => loop.Process(e =>
            {
                iterations++;
                e.Properties["i"] = (int)e.Properties["i"]! + 1;
            }))
            .Process(_ => { }));
        await _context.Start();

        var exchange = new Exchange(new Message("payload"));
        exchange.Properties["i"] = 0;
        await Send("direct://loop-while-nested", exchange);

        iterations.Should().Be(2);
    }

    [Fact]
    public async Task LoopWhile_ConditionFalseFromTheStart_RunsNothing()
    {
        var iterations = 0;
        _context.AddRoutes(r => r.From("direct://loop-while-none")
            .LoopWhile("property.i<3").Process(_ => iterations++).EndLoop());
        await _context.Start();

        var exchange = new Exchange(new Message("payload"));
        exchange.Properties["i"] = 99;
        await Send("direct://loop-while-none", exchange);

        iterations.Should().Be(0);
    }

    /// <summary>
    /// <c>LoopExpression</c> takes an iteration count, <c>LoopWhile</c> takes a condition. The
    /// same string means different things to them, which is why they must not share a name.
    /// </summary>
    [Fact]
    public async Task LoopWhile_IsNotTheSameAsLoopExpression()
    {
        var byCount = 0;
        var byCondition = 0;
        _context.AddRoutes(r =>
        {
            r.From("direct://loop-count").Loop("${property.i}").Process(_ => byCount++).EndLoop();
            r.From("direct://loop-cond").LoopWhile("property.i<3")
                .Process(e => { byCondition++; e.Properties["i"] = (int)e.Properties["i"]! + 1; }).EndLoop();
        });
        await _context.Start();

        var counted = new Exchange(new Message("payload"));
        counted.Properties["i"] = 5;
        await Send("direct://loop-count", counted);

        var conditioned = new Exchange(new Message("payload"));
        conditioned.Properties["i"] = 5;
        await Send("direct://loop-cond", conditioned);

        byCount.Should().Be(5, "LoopExpression reads the value as an iteration count");
        byCondition.Should().Be(0, "LoopWhile reads the same value through a condition that is already false");
    }

    [Fact]
    public async Task LoopWhile_WithMalformedCondition_ThrowsWhileTheRouteIsBuilt()
    {
        _context.AddRoutes(r => r.From("direct://loop-broken")
            .LoopWhile("property.i <").Process(_ => { }).EndLoop());

        var start = async () => await _context.Start();

        await start.Should().ThrowAsync<ExpressionCompilationException>();
    }

    // ── Validate ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Validate_WithConditionString_PassesWhenTheConditionHolds()
    {
        object? captured = null;
        _context.AddRoutes(r => r.From("direct://validate-ok")
            .Validate("header.amount>0").Process(e => captured = e.In.Body));
        await _context.Start();

        await Send("direct://validate-ok", WithAmount(5));

        captured.Should().Be("payload");
    }

    [Fact]
    public async Task Validate_WithConditionString_ThrowsOnFailure()
    {
        _context.AddRoutes(r => r.From("direct://validate-throw")
            .Validate("header.amount>0", "amount must be positive").Process(_ => { }));
        await _context.Start();

        var send = async () => await Send("direct://validate-throw", WithAmount(-1));

        (await send.Should().ThrowAsync<ValidationException>())
            .WithMessage("*amount must be positive*");
    }

    [Fact]
    public async Task Validate_WithConditionString_FlagsWhenNotThrowing()
    {
        _context.AddRoutes(r => r.From("direct://validate-soft")
            .Validate("header.amount>0", "amount must be positive", throwOnFailure: false)
            .Process(_ => { }));
        await _context.Start();

        var exchange = await Send("direct://validate-soft", WithAmount(-1));

        exchange.Properties[ValidateProcessor.ValidationResultProperty].Should().Be(false);
        exchange.Properties[ValidateProcessor.ValidationErrorsProperty].Should().Be("amount must be positive");
    }

    [Fact]
    public async Task Validate_WithMalformedCondition_ThrowsWhileTheRouteIsBuilt()
    {
        _context.AddRoutes(r => r.From("direct://validate-broken").Validate("header.amount >").Process(_ => { }));

        var start = async () => await _context.Start();

        await start.Should().ThrowAsync<ExpressionCompilationException>();
    }
}
