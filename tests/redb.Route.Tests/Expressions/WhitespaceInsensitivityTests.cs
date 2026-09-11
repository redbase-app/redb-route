using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Predicates;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// A condition is a boolean expression, so whitespace between its tokens carries no meaning and an
/// operator inside a quoted literal is not an operator. A value is not a condition: a bare string
/// in value position stays a literal, which is what connector options and SetBody depend on.
/// These tests hold both halves of that rule in place.
/// </summary>
[Collection("ExpressionResolver")]
public class WhitespaceInsensitivityTests
{
    private static IExchange CreateExchange()
    {
        var exchange = new Exchange(new Message("payload"));
        exchange.In.Headers["a"] = 42;
        exchange.In.Headers["b"] = 3;
        exchange.In.Headers["name"] = "a > b";
        exchange.In.Headers["phrase"] = "x AND y";
        return exchange;
    }

    private static bool Condition(string condition)
        => PredicateFactory.FromString(condition).Matches(CreateExchange());

    private static object? Value(string expression)
        => new StringExpression(expression).Evaluate<object?>(CreateExchange());

    // ── Condition position: whitespace is not part of the language ────────────

    [Theory]
    [InlineData("header.a>10")]
    [InlineData("header.a > 10")]
    [InlineData("header.a  >  10")]
    [InlineData("header.a\t>\t10")]
    [InlineData("header.a\n>\n10")]
    [InlineData("header.a >\n    10")]
    [InlineData("  header.a>10  ")]
    public void Comparison_ReadsTheSameHoweverItIsSpaced(string condition)
        => Condition(condition).Should().BeTrue();

    [Theory]
    [InlineData("header.a<10")]
    [InlineData("header.a < 10")]
    [InlineData("header.a  <  10")]
    [InlineData("header.a\t<\t10")]
    public void Comparison_ThatDoesNotHold_ReadsTheSameHoweverItIsSpaced(string condition)
        => Condition(condition).Should().BeFalse();

    [Theory]
    [InlineData("header.a>=42")]
    [InlineData("header.a <= 42")]
    [InlineData("header.a!=41")]
    [InlineData("header.a==42")]
    public void EveryComparisonOperator_WorksWithoutSurroundingSpaces(string condition)
        => Condition(condition).Should().BeTrue();

    [Theory]
    [InlineData("header.a>10 AND header.b<5")]
    [InlineData("header.a > 10 AND header.b < 5")]
    [InlineData("header.a>10  AND  header.b<5")]
    [InlineData("header.a>10\tAND\theader.b<5")]
    [InlineData("header.a>10\nAND\nheader.b<5")]
    [InlineData("header.a>10 and header.b<5")]
    public void WordLogic_ReadsTheSameHoweverItIsSpaced(string condition)
        => Condition(condition).Should().BeTrue();

    [Theory]
    [InlineData("(header.a>10) AND (header.b<5)")]
    [InlineData("(header.a > 10) AND (header.b < 5)")]
    public void ParenthesisedGroups_AreUnderstood(string condition)
        => Condition(condition).Should().BeTrue();

    /// <summary>
    /// An operator inside a quoted literal is part of the literal. The header holds the exact
    /// text <c>a &gt; b</c>, so the comparison has to succeed rather than being read as a chain of
    /// comparisons.
    /// </summary>
    [Fact]
    public void OperatorInsideQuotedLiteral_IsNotAnOperator()
    {
        Condition("header.name == 'a > b'").Should().BeTrue();
        Condition("header.name=='a > b'").Should().BeTrue();
        Condition("header.name == 'a < b'").Should().BeFalse();
    }

    [Fact]
    public void WordLogicInsideQuotedLiteral_IsNotAnOperator()
    {
        Condition("header.phrase == 'x AND y'").Should().BeTrue();
        Condition("header.phrase == 'x OR y'").Should().BeFalse();
    }

    // ── Condition position: no operator means a value read ────────────────────

    /// <summary>
    /// A condition carrying no operator is a value read for truthiness, not a boolean expression.
    /// That distinction is load-bearing: the AST parser does not resolve a CLR member or a
    /// dictionary entry behind a header or property, so sending every condition through it would
    /// turn working conditions such as <c>property.cfg.enabled</c> into a constant false.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConditionWithoutOperator_ResolvesAMemberBehindAHeader(bool active)
    {
        var exchange = new Exchange(new Message("payload"));
        exchange.In.Headers["user"] = new { Active = active };

        PredicateFactory.FromString("header.user.Active").Matches(exchange).Should().Be(active);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConditionWithoutOperator_ResolvesAnEntryBehindAProperty(bool enabled)
    {
        var exchange = new Exchange(new Message("payload"));
        exchange.Properties["cfg"] = new Dictionary<string, object?> { ["enabled"] = enabled };

        PredicateFactory.FromString("property.cfg.enabled").Matches(exchange).Should().Be(enabled);
    }

    [Fact]
    public void ConditionWithoutOperator_ResolvesAHeaderWhoseNameContainsADot()
    {
        var exchange = new Exchange(new Message("payload"));
        exchange.In.Headers["dotted.key"] = "present";

        PredicateFactory.FromString("header.dotted.key").Matches(exchange).Should().BeTrue();
    }

    // ── Value position: a bare string stays a literal ─────────────────────────

    /// <summary>
    /// An explicit expression (<c>Expr(...)</c>, i.e. <see cref="StringExpression"/>) that carries
    /// no operator is a literal, whatever else it contains.
    /// </summary>
    [Theory]
    [InlineData("plain text")]
    [InlineData("hello")]
    [InlineData("dGVzdC1zZWNyZXQ=")]
    [InlineData("key=value")]
    public void ExplicitExpression_WithoutAnOperator_StaysALiteral(string literal)
        => Value(literal).Should().Be(literal);

    /// <summary>
    /// An explicit expression that carries an operator is an expression, whatever the spacing:
    /// "a string is a string, an expression is an expression". Until 2026-08-28 these read as
    /// literals because the value dialect only saw operators surrounded by single spaces — the
    /// last place in the language where whitespace carried meaning. The guarantee that a plain
    /// string stays a literal now lives where it belongs, on the string position:
    /// <c>SetHeader("k", "a>b")</c> is covered end-to-end in <c>ValueAndValidateEndToEndTests</c>.
    /// </summary>
    [Theory]
    [InlineData("a>b", false)]
    [InlineData("x<y", false)]
    [InlineData("a==b", true)]
    [InlineData("black and white", false)]
    [InlineData("yes or no", false)]
    public void ExplicitExpression_WithAnOperator_IsAnExpression(string expression, bool expected)
        => Value(expression).Should().Be(expected);

    /// <summary>
    /// An explicit expression that carries an operator but does not parse fails while it is being
    /// built. It used to compile to a mangled lookup and yield null or its own text on every
    /// message; failing early is the only honest answer for text that means nothing.
    /// </summary>
    [Theory]
    [InlineData("<xml>")]
    [InlineData("<root/>")]
    [InlineData("dGVzdC1zZWNyZXQ==")]
    public void ExplicitExpression_WithAnOperatorThatDoesNotParse_FailsAtBuildTime(string expression)
    {
        var build = () => new StringExpression(expression);

        build.Should().Throw<ExpressionCompilationException>();
    }

    [Theory]
    [InlineData("header.a+header.b", 45)]
    [InlineData("header.a + header.b", 45)]
    [InlineData("header.a*2", 84)]
    [InlineData("header.a * 2", 84)]
    [InlineData("header.a-2", 40)]
    [InlineData("header.a - 2", 40)]
    [InlineData("header.a/2", 21)]
    public void ValuePosition_ArithmeticIsUnchanged(string expression, int expected)
        => Value(expression).Should().Be(expected);

    [Fact]
    public void ValuePosition_TemplateIsUnchanged()
        => Value("${header.a} > 10").Should().Be("42 > 10");
}
