using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Expressions;
using redb.Route.Predicates;

namespace redb.Route.Tests.Dsl;

/// <summary>
/// The <see cref="IConditionSource"/> contract: every scope driven by a condition records what the
/// author wrote under one name and one fixed type, so a reader never has to guess and an
/// introspecting consumer can match on a single type instead of enumerating definition classes.
/// </summary>
// Shares the process-wide ExpressionResolver caches with the other expression tests, so it runs
// in the same collection: cache-statistics assertions there are global and must not race.
[Collection("ExpressionResolver")]
public class ConditionSourceTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static IExchange WithFlag(bool flag)
    {
        var exchange = new Exchange(new Message("payload"));
        exchange.In.Headers["flag"] = flag;
        return exchange;
    }

    // ── Filter ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Filter_FromString_CapturesTemplateAndPredicate()
    {
        FilterDefinition? filter = null;
        _context.AddRoutes(r => filter = r.From("direct://cs-filter-string").Filter("header.flag"));
        await _context.Start();

        filter!.SourceTemplate.Should().Be("header.flag");
        filter.SourcePredicate.Should().NotBeNull();
        filter.SourceExpression.Should().BeNull();
    }

    [Fact]
    public async Task Filter_FromExpression_CapturesExpressionOnly()
    {
        var expression = new HeaderExpression("flag");
        FilterDefinition? filter = null;
        _context.AddRoutes(r => filter = r.From("direct://cs-filter-expr").Filter(expression));
        await _context.Start();

        filter!.SourceExpression.Should().BeSameAs(expression);
        filter.SourceTemplate.Should().BeNull();
        filter.SourcePredicate.Should().BeNull();
    }

    [Fact]
    public async Task Filter_FromPredicate_CapturesPredicateOnly()
    {
        var predicate = new HeaderExpression("flag").isEqualTo(true);
        FilterDefinition? filter = null;
        _context.AddRoutes(r => filter = r.From("direct://cs-filter-pred").Filter(predicate));
        await _context.Start();

        filter!.SourcePredicate.Should().BeSameAs(predicate);
        filter.SourceExpression.Should().BeNull();
        filter.SourceTemplate.Should().BeNull();
    }

    [Fact]
    public async Task Filter_FromDelegate_CapturesNothing()
    {
        FilterDefinition? filter = null;
        _context.AddRoutes(r => filter = r.From("direct://cs-filter-lambda").Filter(_ => true));
        await _context.Start();

        filter!.SourcePredicate.Should().BeNull("a lambda has no source to capture");
        filter.SourceExpression.Should().BeNull();
        filter.SourceTemplate.Should().BeNull();
    }

    // ── When ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task When_FromString_CapturesTemplateAndPredicate()
    {
        WhenDefinition? branch = null;
        _context.AddRoutes(r => branch = r.From("direct://cs-when-string").Choice().When("header.flag"));
        await _context.Start();

        branch!.SourceTemplate.Should().Be("header.flag");
        branch.SourcePredicate.Should().NotBeNull();
        branch.SourceExpression.Should().BeNull();
    }

    /// <summary>
    /// Before the contract landed this path recorded nothing at all: the expression was folded into
    /// a delegate and lost.
    /// </summary>
    [Fact]
    public async Task When_FromExpression_CapturesExpressionOnly()
    {
        var expression = new HeaderExpression("flag");
        WhenDefinition? branch = null;
        _context.AddRoutes(r => branch = r.From("direct://cs-when-expr").Choice().When(expression));
        await _context.Start();

        branch!.SourceExpression.Should().BeSameAs(expression);
        branch.SourceTemplate.Should().BeNull();
        branch.SourcePredicate.Should().BeNull();
    }

    [Fact]
    public async Task When_FromPredicate_CapturesPredicateOnly()
    {
        var predicate = new HeaderExpression("flag").isEqualTo(true);
        WhenDefinition? branch = null;
        _context.AddRoutes(r => branch = r.From("direct://cs-when-pred").Choice().When(predicate));
        await _context.Start();

        branch!.SourcePredicate.Should().BeSameAs(predicate);
        branch.SourceExpression.Should().BeNull();
        branch.SourceTemplate.Should().BeNull();
    }

    [Fact]
    public async Task When_FromDelegate_CapturesNothing()
    {
        WhenDefinition? branch = null;
        _context.AddRoutes(r => branch = r.From("direct://cs-when-lambda").Choice().When(_ => true));
        await _context.Start();

        branch!.SourcePredicate.Should().BeNull();
        branch.SourceExpression.Should().BeNull();
        branch.SourceTemplate.Should().BeNull();
    }

    [Fact]
    public async Task AliasWhen_FromExpression_CapturesExpressionOnly()
    {
        var expression = new HeaderExpression("flag");
        WhenDefinition? branch = null;
        _context.AddRoutes(r =>
        {
            var choice = r.From("direct://cs-when-alias").Choice();
            var nested = choice.When("header.flag").Filter("header.flag");
            branch = nested.When(expression);
        });
        await _context.Start();

        branch!.SourceExpression.Should().BeSameAs(expression);
    }

    // ── The contract itself ───────────────────────────────────────────────────

    /// <summary>
    /// Both condition scopes are reachable through the same type, which is the whole point: an
    /// introspecting consumer matches on <see cref="IConditionSource"/> rather than enumerating
    /// definition classes and hoping the property names line up.
    /// </summary>
    [Fact]
    public async Task BothConditionScopes_AreReadableThroughOneContract()
    {
        var scopes = new List<IProcessorDefinition>();
        _context.AddRoutes(r =>
        {
            scopes.Add(r.From("direct://cs-contract-filter").Filter("header.flag"));
            scopes.Add(r.From("direct://cs-contract-when").Choice().When("header.flag"));
        });
        await _context.Start();

        scopes.Should().AllBeAssignableTo<IConditionSource>();

        foreach (var source in scopes.Cast<IConditionSource>())
        {
            source.SourceTemplate.Should().Be("header.flag");
            source.SourceExpression.Should().BeNull();
            source.SourcePredicate.Should().NotBeNull();
            source.SourcePredicate!.Matches(WithFlag(true)).Should().BeTrue();
            source.SourcePredicate.Matches(WithFlag(false)).Should().BeFalse();
        }
    }

    /// <summary>
    /// The contract is read-only: nothing outside the assembly can rewrite what a route recorded.
    /// </summary>
    [Fact]
    public void TheContractExposesNoSetters()
        => typeof(IConditionSource).GetProperties()
            .Should().OnlyContain(p => p.CanRead && !p.CanWrite);
}
