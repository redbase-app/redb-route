using AwesomeAssertions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Predicates;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// Text after a complete expression (findings register row 32): until 4.7.0 the engine stopped at the end of the
/// expression and dropped the rest, so <c>header.b == 3 garbage</c> compared and <c>${exception.type|message}</c>
/// rendered empty. Now the rest is an error naming it and its position, when the route is built.
/// </summary>
public sealed class TrailingTextTests
{
    private static Exchange Exchange()
    {
        var exchange = new Exchange(new Message("x"));
        exchange.In.Headers["b"] = 3;
        exchange.In.Headers["Content-Type"] = "text/xml";
        exchange.In.Headers["user"] = new Dictionary<string, object?> { ["name"] = "Ann" };
        return exchange;
    }

    [Theory]
    [InlineData("${header.b == 3 garbage}", "unexpected 'garbage' at position 15 within the expression")]
    [InlineData("${exception.type|message}", "unexpected '|' at position 15 within the expression")]
    [InlineData("${upper(header.s) x}", "unexpected 'x' at position 17 within the expression")]
    [InlineData("total ${header.a 7}", "unexpected '7' at position 10 within the expression")]
    [InlineData("${a ${b}}", "unexpected '${' at position 3 within the expression")]
    public void InAPlaceholder_TheRestIsAnError(string template, string detail)
    {
        var act = () => ExpressionResolver.ProcessTemplate(template, Exchange());

        act.Should().Throw<ExpressionCompilationException>().WithMessage($"*{detail}: the expression ends before it.");
    }

    [Theory]
    [InlineData("header.b == 3 garbage")]
    [InlineData("header.b == 3 'x'")]
    [InlineData("upper(header.s) x")]
    public void InAValueAndACondition_TheRestIsAnError(string expression)
    {
        var value = () => ExpressionResolver.GetCompiledValueExpression(expression);
        var condition = () => PredicateFactory.FromString(expression);

        value.Should().Throw<ExpressionCompilationException>().WithMessage("*the expression ends before it.");
        condition.Should().Throw<ExpressionCompilationException>().WithMessage("*the expression ends before it.");
    }

    [Theory]
    [InlineData("header.b 3")]
    [InlineData("plain text")]
    public void BareTextWithoutAnOperator_StaysTheText(string expression)
    {
        ExpressionResolver.GetCompiledValueExpression(expression)(Exchange()).Should().Be(expression, "the bare-text rule of row 29");
    }

    [Fact]
    public void AnUppercaseFileKey_GetsAHint()
    {
        var act = () => ExpressionResolver.ProcessTemplate("${FILE:Name.NoExt}", Exchange());

        act.Should().Throw<ExpressionCompilationException>().WithMessage("${FILE:Name.NoExt}: file keys are lowercase: ${file:name.noext}.");
    }

    // ── the literal-name-first rule keeps its names ─────────────────

    [Theory]
    [InlineData("${header.Content-Type}", "text/xml")]
    [InlineData("${header.user.name}", "Ann")]
    [InlineData("[${header.missing-name}]", "[]")]
    [InlineData("[${header.no.such.path}]", "[]")]
    public void OneWordNames_StillReadTheHeader(string template, string expected)
    {
        ExpressionResolver.ProcessTemplate(template, Exchange()).Should().Be(expected);
    }

    [Fact]
    public void AOneWordTailThatDoesNotCompile_IsANameLookup()
    {
        var exchange = Exchange();
        exchange.In.Headers["a#b"] = "hash";

        ExpressionResolver.ProcessTemplate("${header.a#b}", exchange).Should().Be("hash");
    }

    [Fact]
    public void ATailWithWhitespaceThatDoesNotCompile_IsTheError_NotAMissingName()
    {
        var act = () => ExpressionResolver.ProcessTemplate("${header.b == 3 garbage}", Exchange());

        act.Should().Throw<ExpressionCompilationException>();
    }
}
