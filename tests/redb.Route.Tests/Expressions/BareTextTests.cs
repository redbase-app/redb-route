using AwesomeAssertions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Predicates;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// Bare text in a value or condition (findings register row 29): a text without an arithmetic operator is what it was
/// (a lookup, a literal, or the text itself, as in Apache Camel); a text with one must be whole arithmetic, else a
/// positioned error. The old compiler used to compute pieces: <c>1 +</c> gave <c>1</c>, <c>header.a+b</c> gave
/// <c>2b</c>, <c>header.a * x</c> gave <c>xx</c>.
/// </summary>
public sealed class BareTextTests
{
    private static Exchange Message()
    {
        var exchange = new Exchange(new Message("x"));
        exchange.In.Headers["a"] = 2;
        exchange.In.Headers["b"] = 3;
        exchange.In.Headers["Content-Type"] = "text/xml";
        return exchange;
    }

    private static object? Value(string expression) => ExpressionResolver.GetCompiledValueExpression(expression)(Message());

    [Theory]
    [InlineData("hello")]
    [InlineData("plain text")]
    [InlineData("Hello World")]
    [InlineData("key=value")]
    [InlineData("((((")]
    [InlineData("~~~INVALID~~~")]
    [InlineData("dGVzdC1zZWNyZXQ=")]
    [InlineData("${}")]
    [InlineData("'unterminated")]
    public void TextWithoutArithmetic_IsTheText(string text)
    {
        // Every value expression is the AST's since row 28; the row 29 rule keeps these as the text itself there too.
        Value(text).Should().Be(text);
        PredicateFactory.FromString(text).Matches(Message()).Should().BeTrue("non-empty text is true");
    }

    [Fact]
    public void ABareExceptionMessage_ReadsTheException()
    {
        // Row 28: the old compiler returned the text "exception.Message"; the AST reads the root, as ${...} does.
        var failed = Message();
        failed.Exception = new InvalidOperationException("boom");

        ExpressionResolver.GetCompiledValueExpression("exception.Message")(failed).Should().Be("boom");
        ExpressionResolver.GetCompiledValueExpression("exception.Message")(Message()).Should().BeNull();
    }

    [Theory]
    [InlineData("direct://cat-all-2")]
    [InlineData("direct://a,direct://b")]
    [InlineData("kafka:orders/eu")]
    public void AnEndpointUri_IsNotArithmetic(string uri)
    {
        // A destination named in an expression (<recipientList expr="direct://a,direct://b"/>) keeps compiling.
        var act = () => ExpressionResolver.GetCompiledValueExpression(uri);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("header.a + 1", 3)]
    [InlineData("header.a+header.b", 5)]
    [InlineData("header.a * 2", 4)]
    [InlineData("10 / header.a", 5)]
    public void WholeArithmetic_IsComputedAsBefore(string expression, int expected)
    {
        System.Convert.ToInt32(Value(expression)).Should().Be(expected);
    }

    [Fact]
    public void TextPlusANumber_IsConcatenatedAsBefore()
    {
        Value("body + 1").Should().Be("x1");
    }

    [Theory]
    [InlineData("1 +", "the expression ends after '+' at position 3, where a value is expected within the expression \"1 +\".")]
    [InlineData("header.a +", "the expression ends after '+' at position 10, where a value is expected within the expression \"header.a +\".")]
    [InlineData("1 + 2 3", "unexpected '3' at position 7 within the expression \"1 + 2 3\".")]
    [InlineData("header.a+b", "'b' at position 10 is not a value: write header.b, property.b or the text 'b' in quotes within the expression \"header.a+b\".")]
    [InlineData("header.a * x", "'x' at position 12 is not a value: write header.x, property.x or the text 'x' in quotes within the expression \"header.a * x\".")]
    [InlineData("application/json", "'application' at position 1 is not a value: write header.application, property.application or the text 'application' in quotes within the expression \"application/json\".")]
    [InlineData("header.Content-Type", "'header.Content-Type' at position 1 reads as a subtraction: a name with a dash is written ${header.Content-Type} within the expression \"header.Content-Type\".")]
    [InlineData("header.X-Request-Id", "'header.X-Request-Id' at position 1 reads as a subtraction: a name with a dash is written ${header.X-Request-Id} within the expression \"header.X-Request-Id\".")]
    public void ArithmeticThatBreaksOff_IsAPositionedError(string expression, string message)
    {
        var act = () => ExpressionResolver.GetCompiledValueExpression(expression);

        act.Should().Throw<ExpressionCompilationException>().WithMessage(message);
    }

    [Fact]
    public void InACondition_TheSameError()
    {
        var act = () => PredicateFactory.FromString("header.a+b");

        act.Should().Throw<ExpressionCompilationException>().WithMessage("'b' at position 10 is not a value*");
    }

    [Fact]
    public void ADashedNameInAPlaceholder_StillReadsTheHeader()
    {
        new StringExpression("${header.Content-Type}").Evaluate<object>(Message()).Should().Be("text/xml");
    }
}
