using AwesomeAssertions;
using redb.Route.Core;
using redb.Route.Expressions;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// The operand of <c>++</c> / <c>--</c> (findings register row 23): a missing value starts at 0, a number or a numeric
/// text counts, and a value that is there but not a number is an error naming it instead of silently becoming 0.
/// </summary>
public sealed class IncrementOperandTests
{
    private static Exchange Message(object? value)
    {
        var exchange = new Exchange(new Message("x"));
        if (value is not null)
            exchange.In.Headers["x"] = value;
        return exchange;
    }

    [Theory]
    [InlineData(null, 0, 1)]
    [InlineData(5, 5, 6)]
    [InlineData("7", 7, 8)]
    public void ACounter_Counts(object? start, int returns, int after)
    {
        var exchange = Message(start);

        Convert.ToInt32(new StringExpression("header.x++").Evaluate<object>(exchange)).Should().Be(returns);
        Convert.ToInt32(exchange.In.Headers["x"]).Should().Be(after);
    }

    [Theory]
    [InlineData("header.x++", "header.x++")]
    [InlineData("++header.x", "++header.x")]
    [InlineData("header.x--", "header.x--")]
    [InlineData("--header.x", "--header.x")]
    public void ANonNumericValue_IsAnErrorNamingIt(string expression, string written)
    {
        var exchange = Message("abc");
        var act = () => new StringExpression(expression).Evaluate<object>(exchange);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"{written}: the value 'abc' (String) is not a number.");
        exchange.In.Headers["x"].Should().Be("abc", "the value is left as it was");
    }

    [Fact]
    public void InATemplate_TheSameError()
    {
        var act = () => ExpressionResolver.ProcessTemplate("n=${header.x++}", Message("abc"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*is not a number.");
    }
}
