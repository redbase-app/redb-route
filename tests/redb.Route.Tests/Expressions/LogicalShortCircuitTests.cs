using AwesomeAssertions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Expressions.Ast;
using redb.Route.Predicates;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// AND and OR short-circuit over the truthiness rule (findings register row 25), and ?? evaluates its right side only
/// when the left is null (row 26): the right side runs only when the left
/// one does not decide. The values are the ones the language gave before; what stops is a side effect or a fault on a
/// right side that does not matter. XOR needs both sides and evaluates both.
/// </summary>
public sealed class LogicalShortCircuitTests
{
    private static Exchange Message(bool flag)
    {
        var exchange = new Exchange(new Message("x"));
        exchange.In.Headers["flag"] = flag;
        exchange.In.Headers["n"] = 0;
        return exchange;
    }

    [Theory]
    [InlineData("header.flag AND header.n++ > 0", false, 0)]
    [InlineData("header.flag && header.n++ > 0", false, 0)]
    [InlineData("header.flag OR header.n++ > 0", true, 0)]
    [InlineData("header.flag || header.n++ > 0", true, 0)]
    [InlineData("header.flag AND header.n++ > 0", true, 1)]
    [InlineData("header.flag OR header.n++ > 0", false, 1)]
    [InlineData("header.flag XOR header.n++ > 0", false, 1)]
    [InlineData("header.flag XOR header.n++ > 0", true, 1)]
    public void TheRightSide_RunsOnlyWhenTheLeftDoesNotDecide(string expression, bool flag, int nAfter)
    {
        var exchange = Message(flag);

        new StringExpression(expression).Evaluate<object>(exchange);

        exchange.In.Headers["n"].Should().Be(nAfter);
    }

    [Theory]
    [InlineData("header.flag AND bean:svc.Check()", false)]
    [InlineData("header.flag OR bean:svc.Check()", true)]
    public void AFaultOnARightSideThatDoesNotMatter_DoesNotHappen(string condition, bool flag)
    {
        // The exchange has no route context, so evaluating the bean: call would fail.
        PredicateFactory.FromString(condition).Matches(Message(flag)).Should().Be(flag);
    }

    [Theory]
    [InlineData("header.flag AND bean:svc.Check()", true)]
    [InlineData("header.flag OR bean:svc.Check()", false)]
    public void ARightSideThatMatters_StillRunsAndFails(string condition, bool flag)
    {
        var act = () => PredicateFactory.FromString(condition).Matches(Message(flag));

        act.Should().Throw<InvalidOperationException>().WithMessage("*has no route context*");
    }

    [Theory]
    [InlineData("header.flag ?? header.n++", true, 0)]
    [InlineData("header.missing ?? header.n++", true, 1)]
    public void NullCoalesce_RunsItsRightSideOnlyForANullLeft(string expression, bool flag, int nAfter)
    {
        var exchange = Message(flag);

        new StringExpression(expression).Evaluate<object>(exchange);

        exchange.In.Headers["n"].Should().Be(nAfter);
    }

    [Fact]
    public void NullCoalesce_AFaultOnTheRightSide_HappensOnlyWhenTheLeftIsNull()
    {
        new StringExpression("header.flag ?? bean:svc.Check()").Evaluate<object>(Message(true)).Should().Be(true);

        var act = () => new StringExpression("header.missing ?? bean:svc.Check()").Evaluate<object>(Message(true));
        act.Should().Throw<InvalidOperationException>().WithMessage("*has no route context*");
    }

    [Theory]
    [InlineData("header.missing ?? 'default'", "default")]
    [InlineData("header.flag ?? 'default'", true)]
    [InlineData("header.n ?? 5", 0)]
    public void NullCoalesce_TheValues_AreLeftOrRight(string expression, object expected)
    {
        new StringExpression(expression).Evaluate<object>(Message(true)).Should().Be(expected);
    }

    [Theory]
    [InlineData("null AND 'x'", false)]
    [InlineData("'x' AND 1", true)]
    [InlineData("0 OR 'no'", false)]
    [InlineData("0 OR 'anything'", true)]
    [InlineData("'yes' AND 'on'", true)]
    [InlineData("header.missing OR header.flag", true)]
    public void TheValues_AreTheTruthinessRule(string expression, bool expected)
    {
        new StringExpression(expression).Evaluate<object>(Message(true)).Should().Be(expected);
    }

    [Theory]
    [InlineData("header.n++", 0, 1)]
    [InlineData("++header.n", 1, 1)]
    [InlineData("header.n--", 0, -1)]
    public void TheInterpretedForm_IsTheCompiledOne_IncrementsWriteBack(string expression, int returns, int nAfter)
    {
        // Findings register row 27: Node.Evaluate used to be a second evaluator whose ++/-- did not write back.
        var exchange = Message(true);
        var node = new Parser(new Tokenizer(expression).GetAllTokens()).Parse();

        Convert.ToInt32(node.Evaluate(exchange)).Should().Be(returns);
        exchange.In.Headers["n"].Should().BeEquivalentTo(nAfter);
    }

    [Theory]
    [InlineData("header.flag AND bean:svc.Check()", false, false)]
    [InlineData("header.flag OR bean:svc.Check()", true, false)]
    [InlineData("header.flag AND bean:svc.Check()", true, true)]
    [InlineData("header.flag OR bean:svc.Check()", false, true)]
    public void TheInterpretedForm_ShortCircuitsTheSameWay(string expression, bool flag, bool rightRuns)
    {
        // The right side fails when it runs (no route context for the bean: call), which shows whether it ran.
        var node = new Parser(new Tokenizer(expression).GetAllTokens()).Parse();

        var act = () => node.Evaluate(Message(flag));

        if (rightRuns)
            act.Should().Throw<InvalidOperationException>().WithMessage("*has no route context*");
        else
            act.Should().NotThrow();
    }
}
