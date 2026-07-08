using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Predicates;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// Tests for all 19 Predicate classes.
/// </summary>
public class PredicateTests
{
    private static IExchange CreateExchange(object? body = null)
        => new Exchange(new Message(body));

    // ── EqualsPredicate ──

    [Fact]
    public void Equals_Matches_WhenEqual()
    {
        var pred = new EqualsPredicate(new BodyExpression(), "hello");
        pred.Matches(CreateExchange("hello")).Should().BeTrue();
    }

    [Fact]
    public void Equals_DoesNotMatch_WhenNotEqual()
    {
        var pred = new EqualsPredicate(new BodyExpression(), "hello");
        pred.Matches(CreateExchange("world")).Should().BeFalse();
    }

    // ── NotEqualsPredicate ──

    [Fact]
    public void NotEquals_Matches_WhenDifferent()
    {
        var pred = new NotEqualsPredicate(new BodyExpression(), "hello");
        pred.Matches(CreateExchange("world")).Should().BeTrue();
    }

    [Fact]
    public void NotEquals_DoesNotMatch_WhenSame()
    {
        var pred = new NotEqualsPredicate(new BodyExpression(), "hello");
        pred.Matches(CreateExchange("hello")).Should().BeFalse();
    }

    // ── GreaterThanPredicate ──

    [Fact]
    public void GreaterThan_Matches_WhenGreater()
    {
        var pred = new GreaterThanPredicate(new BodyExpression(), 5);
        pred.Matches(CreateExchange(10)).Should().BeTrue();
    }

    [Fact]
    public void GreaterThan_DoesNotMatch_WhenLessOrEqual()
    {
        var pred = new GreaterThanPredicate(new BodyExpression(), 5);
        pred.Matches(CreateExchange(5)).Should().BeFalse();
    }

    // ── GreaterThanOrEqualPredicate ──

    [Fact]
    public void GreaterThanOrEqual_Matches_WhenEqual()
    {
        var pred = new GreaterThanOrEqualPredicate(new BodyExpression(), 5);
        pred.Matches(CreateExchange(5)).Should().BeTrue();
    }

    // ── LessThanPredicate ──

    [Fact]
    public void LessThan_Matches_WhenLess()
    {
        var pred = new LessThanPredicate(new BodyExpression(), 10);
        pred.Matches(CreateExchange(5)).Should().BeTrue();
    }

    [Fact]
    public void LessThan_DoesNotMatch_WhenGreaterOrEqual()
    {
        var pred = new LessThanPredicate(new BodyExpression(), 10);
        pred.Matches(CreateExchange(10)).Should().BeFalse();
    }

    // ── LessThanOrEqualPredicate ──

    [Fact]
    public void LessThanOrEqual_Matches_WhenEqual()
    {
        var pred = new LessThanOrEqualPredicate(new BodyExpression(), 10);
        pred.Matches(CreateExchange(10)).Should().BeTrue();
    }

    // ── BetweenPredicate ──

    [Fact]
    public void Between_Matches_WhenInRange()
    {
        var pred = new BetweenPredicate(new BodyExpression(), 5, 15);
        pred.Matches(CreateExchange(10)).Should().BeTrue();
    }

    [Fact]
    public void Between_Matches_WhenOnBoundary()
    {
        var pred = new BetweenPredicate(new BodyExpression(), 5, 15);
        pred.Matches(CreateExchange(5)).Should().BeTrue();
        pred.Matches(CreateExchange(15)).Should().BeTrue();
    }

    [Fact]
    public void Between_DoesNotMatch_WhenOutOfRange()
    {
        var pred = new BetweenPredicate(new BodyExpression(), 5, 15);
        pred.Matches(CreateExchange(20)).Should().BeFalse();
    }

    // ── ContainsPredicate ──

    [Fact]
    public void Contains_Matches_WhenSubstringPresent()
    {
        var pred = new ContainsPredicate(new BodyExpression(), "world");
        pred.Matches(CreateExchange("hello world")).Should().BeTrue();
    }

    [Fact]
    public void Contains_DoesNotMatch_WhenAbsent()
    {
        var pred = new ContainsPredicate(new BodyExpression(), "xyz");
        pred.Matches(CreateExchange("hello")).Should().BeFalse();
    }

    // ── StartsWithPredicate ──

    [Fact]
    public void StartsWith_Matches()
    {
        var pred = new StartsWithPredicate(new BodyExpression(), "hel");
        pred.Matches(CreateExchange("hello")).Should().BeTrue();
    }

    [Fact]
    public void StartsWith_DoesNotMatch()
    {
        var pred = new StartsWithPredicate(new BodyExpression(), "xyz");
        pred.Matches(CreateExchange("hello")).Should().BeFalse();
    }

    // ── EndsWithPredicate ──

    [Fact]
    public void EndsWith_Matches()
    {
        var pred = new EndsWithPredicate(new BodyExpression(), "llo");
        pred.Matches(CreateExchange("hello")).Should().BeTrue();
    }

    // ── RegexPredicate ──

    [Fact]
    public void Regex_Matches()
    {
        var pred = new RegexPredicate(new BodyExpression(), @"^\d{3}$");
        pred.Matches(CreateExchange("123")).Should().BeTrue();
    }

    [Fact]
    public void Regex_DoesNotMatch()
    {
        var pred = new RegexPredicate(new BodyExpression(), @"^\d{3}$");
        pred.Matches(CreateExchange("12")).Should().BeFalse();
    }

    // ── InPredicate ──

    [Fact]
    public void In_Matches_WhenValueInList()
    {
        var pred = new InPredicate(new BodyExpression(), new object[] { "a", "b", "c" });
        pred.Matches(CreateExchange("b")).Should().BeTrue();
    }

    [Fact]
    public void In_DoesNotMatch_WhenAbsent()
    {
        var pred = new InPredicate(new BodyExpression(), new object[] { "a", "b", "c" });
        pred.Matches(CreateExchange("d")).Should().BeFalse();
    }

    // ── IsNullPredicate ──

    [Fact]
    public void IsNull_Matches_WhenNull()
    {
        var pred = new IsNullPredicate(new BodyExpression());
        pred.Matches(CreateExchange(null)).Should().BeTrue();
    }

    [Fact]
    public void IsNull_DoesNotMatch_WhenNotNull()
    {
        var pred = new IsNullPredicate(new BodyExpression());
        pred.Matches(CreateExchange("value")).Should().BeFalse();
    }

    // ── IsNotNullPredicate ──

    [Fact]
    public void IsNotNull_Matches_WhenNotNull()
    {
        var pred = new IsNotNullPredicate(new BodyExpression());
        pred.Matches(CreateExchange("value")).Should().BeTrue();
    }

    [Fact]
    public void IsNotNull_DoesNotMatch_WhenNull()
    {
        var pred = new IsNotNullPredicate(new BodyExpression());
        pred.Matches(CreateExchange(null)).Should().BeFalse();
    }

    // ── NotPredicate ──

    [Fact]
    public void Not_InvertsResult()
    {
        var inner = new DelegateExpression<bool>(ex => ex.In.Body?.ToString() == "hello");
        var pred = new NotPredicate(inner);
        pred.Matches(CreateExchange("hello")).Should().BeFalse();
        pred.Matches(CreateExchange("bye")).Should().BeTrue();
    }

    // ── AndPredicate ──

    [Fact]
    public void And_MatchesWhenBothTrue()
    {
        var left = new DelegateExpression<bool>(ex => ex.In.Body is string s && s.Length > 2);
        var right = new LambdaPredicate(ex => ex.In.Body is string s && s.StartsWith("h"));
        var pred = new AndPredicate(left, right);
        pred.Matches(CreateExchange("hello")).Should().BeTrue();
    }

    [Fact]
    public void And_DoesNotMatchWhenOneFalse()
    {
        var left = new DelegateExpression<bool>(_ => true);
        var right = new LambdaPredicate(_ => false);
        var pred = new AndPredicate(left, right);
        pred.Matches(CreateExchange("x")).Should().BeFalse();
    }

    // ── OrPredicate ──

    [Fact]
    public void Or_MatchesWhenEitherTrue()
    {
        var left = new DelegateExpression<bool>(_ => false);
        var right = new LambdaPredicate(_ => true);
        var pred = new OrPredicate(left, right);
        pred.Matches(CreateExchange("x")).Should().BeTrue();
    }

    [Fact]
    public void Or_DoesNotMatchWhenBothFalse()
    {
        var left = new DelegateExpression<bool>(_ => false);
        var right = new LambdaPredicate(_ => false);
        var pred = new OrPredicate(left, right);
        pred.Matches(CreateExchange("x")).Should().BeFalse();
    }

    // ── LambdaPredicate ──

    [Fact]
    public void Lambda_ExecutesDelegate()
    {
        var pred = new LambdaPredicate(ex => (int)ex.In.Body! > 5);
        pred.Matches(CreateExchange(10)).Should().BeTrue();
        pred.Matches(CreateExchange(3)).Should().BeFalse();
    }

    // ── LogicalPredicate ──

    [Fact]
    public void LogicalPredicate_EvaluatesExpressionString()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["count"] = 10;
        var pred = new LogicalPredicate("property.count == 10");
        pred.Matches(exchange).Should().BeTrue();
    }

    // ── Async ──

    [Fact]
    public async Task Equals_MatchesAsync()
    {
        var pred = new EqualsPredicate(new BodyExpression(), "hello");
        (await pred.MatchesAsync(CreateExchange("hello"))).Should().BeTrue();
    }
}
