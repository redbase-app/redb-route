using AwesomeAssertions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Expressions.Ast;
using redb.Route.Predicates;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// The comparison operators of Apache Camel's Simple: <c>=~</c> (equals ignoring case: two strings ignore case, any
/// other pair is <c>==</c>) and <c>range</c> / <c>not range</c> over a quoted <c>'from..to'</c> literal of two
/// numbers, inclusive, read at compile time. <c>range</c> is an operator only right after an operand.
/// </summary>
public sealed class EqualsIgnoreCaseAndRangeTests
{
    private static Exchange Message()
    {
        var exchange = new Exchange(new Message("payload"));
        exchange.In.Headers["s"] = "Hello";
        exchange.In.Headers["n"] = 7;
        exchange.In.Headers["d"] = 2.5;
        exchange.In.Headers["m"] = 10m;
        exchange.In.Headers["t"] = "12";
        exchange.In.Headers["word"] = "abc";
        exchange.In.Headers["nothing"] = null;
        exchange.In.Headers["range"] = "a header named range";
        return exchange;
    }

    private static bool Condition(string condition) => PredicateFactory.FromString(condition).Matches(Message());

    private static AstNode Parse(string expression) => new Parser(new Tokenizer(expression).GetAllTokens()).Parse();

    [Theory]
    [InlineData("header.s =~ 'hello'", true)]
    [InlineData("header.s =~ 'HELLO'", true)]
    [InlineData("header.s =~ 'help'", false)]
    [InlineData("header.s=~'hello'", true)]
    [InlineData("header.s == 'hello'", false)]
    [InlineData("header.n =~ 7", true)]
    [InlineData("header.n =~ '7'", true)]
    [InlineData("header.n =~ 8", false)]
    [InlineData("header.nothing =~ null", true)]
    [InlineData("header.nothing =~ 'x'", false)]
    [InlineData("!(header.s =~ 'hello')", false)]
    [InlineData("${header.s =~ 'HELLO'}", true)]
    public void EqualsIgnoreCase(string condition, bool expected)
    {
        Condition(condition).Should().Be(expected);
    }

    [Theory]
    [InlineData("header.n range '1..10'", true)]
    [InlineData("header.n range '7..7'", true)]
    [InlineData("header.n range '8..10'", false)]
    [InlineData("header.n range '1..6'", false)]
    [InlineData("header.n RANGE '1..10'", true)]
    [InlineData("header.d range '2.5..3'", true)]
    [InlineData("header.d range '1.5..2.4'", false)]
    [InlineData("header.m range '-5..10'", true)]
    [InlineData("header.t range '10..20'", true)]
    [InlineData("header.n range ' 1 .. 10 '", true)]
    [InlineData("header.n + 5 range '10..12'", true)]
    [InlineData("header.n not range '1..10'", false)]
    [InlineData("header.n NOT RANGE '8..10'", true)]
    [InlineData("header.nothing range '1..10'", false)]
    [InlineData("header.nothing not range '1..10'", true)]
    [InlineData("header.n range '1..10' && header.s =~ 'hello'", true)]
    [InlineData("${header.n range '1..10'}", true)]
    public void Range(string condition, bool expected)
    {
        Condition(condition).Should().Be(expected);
    }

    [Fact]
    public void Range_ATextThatIsNotANumber_IsAnErrorNamingIt()
    {
        var act = () => Condition("header.word range '1..10'");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("header.word range '1..10': the value 'abc' (String) is not a number.");
    }

    [Theory]
    [InlineData("header.n range 5", "range takes a quoted 'from..to' literal, as in header.n range '1..10' (position 16 within the expression).")]
    [InlineData("header.n range", "range takes a quoted 'from..to' literal, as in header.n range '1..10' (position 15 within the expression).")]
    [InlineData("header.n range '1-10'", "range '1-10' is not 'from..to' with two numbers, as in '1..10' (position 16 within the expression).")]
    [InlineData("header.n range 'a..b'", "range 'a..b' is not 'from..to' with two numbers, as in '1..10' (position 16 within the expression).")]
    [InlineData("header.n range '10..1'", "range '10..1' starts above its end: write the smaller number first (position 16 within the expression).")]
    public void Range_AMalformedLiteral_IsAnErrorAtCompile(string expression, string message)
    {
        var act = () => Parse(expression);

        act.Should().Throw<InvalidOperationException>().WithMessage(message);
    }

    [Fact]
    public void Range_IsANameEverywhereElse()
    {
        Parse("header.range").Should().BeOfType<IdentifierNode>();
        Parse("range").Should().BeOfType<IdentifierNode>();
        new StringExpression("${header.range}").Evaluate<object>(Message()).Should().Be("a header named range");
        Parse("not range").Should().NotBeOfType<RangeNode>();
    }

    [Fact]
    public void Range_ReadsItsEndsOnce()
    {
        Parse("header.n not range '1.5..20'").Should().BeOfType<RangeNode>()
            .Which.Should().Match<RangeNode>(r => r.From == 1.5m && r.To == 20m && r.Negated);
    }

    [Theory]
    [InlineData("header.s =~ 'x'")]
    [InlineData("header.n range '1..2'")]
    [InlineData("header.n not range '1..2'")]
    public void TheDetector_SeesTheOperators(string expression)
    {
        ExpressionResolver.ContainsComparisonOrWordLogicOperator(expression).Should().BeTrue();
    }

    [Theory]
    [InlineData("header.range")]
    [InlineData("'a range b'")]
    public void TheDetector_LeavesNamesAndLiterals(string expression)
    {
        ExpressionResolver.ContainsComparisonOrWordLogicOperator(expression).Should().BeFalse();
    }
}
