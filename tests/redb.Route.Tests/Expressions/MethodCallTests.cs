using AwesomeAssertions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Predicates;

namespace redb.Route.Tests.Expressions;

/// <summary>A value with methods of its own, for reflection calls.</summary>
public sealed class MethodTarget
{
    public string Greet(string name) => "hi " + name;

    public long Twice(long value) => value * 2;

    public string Boom() => throw new InvalidOperationException("boom");

    public string Name => "target";
}

/// <summary>
/// Method calls on values in the AST (findings register row 28, step 28a): the built-in dot helpers, a public method
/// of the value's type outside the sandbox, a null value giving null, and a method that throws or does not exist as a
/// positioned error instead of a silent null.
/// </summary>
public sealed class MethodCallTests
{
    private static Exchange Message()
    {
        var exchange = new Exchange(new Message("payload"));
        exchange.In.Headers["s"] = "Hello World";
        exchange.In.Headers["list"] = new List<int> { 3, 5 };
        exchange.In.Headers["o"] = new MethodTarget();
        return exchange;
    }

    private static object? Value(string expression) => ExpressionResolver.GetCompiledValueExpressionWithAst(expression)(Message());

    [Theory]
    [InlineData("header.s.length()", 11)]
    [InlineData("header.s.toUpper()", "HELLO WORLD")]
    [InlineData("header.s.toLower()", "hello world")]
    [InlineData("header.s.substring(0, 5)", "Hello")]
    [InlineData("header.s.replace('o', '0')", "Hell0 W0rld")]
    [InlineData("header.s.indexOf('W')", 6)]
    [InlineData("header.s.startsWith('Hell')", true)]
    [InlineData("header.s.endsWith('x')", false)]
    [InlineData("header.s.contains('lo W')", true)]
    [InlineData("header.list.contains(5)", true)]
    [InlineData("header.list.count()", 2)]
    [InlineData("upper(header.s).length()", 11)]
    public void TheDotHelpers(string expression, object expected)
    {
        Value(expression).Should().Be(expected);
    }

    [Theory]
    [InlineData("header.s.length() > 10", true)]
    [InlineData("header.s.toUpper() == 'HELLO WORLD'", true)]
    public void InACondition(string condition, bool expected)
    {
        PredicateFactory.FromString(condition).Matches(Message()).Should().Be(expected);
    }

    [Fact]
    public void InATemplate()
    {
        ExpressionResolver.ProcessTemplate("[${header.s.toUpper()}]", Message()).Should().Be("[HELLO WORLD]");
    }

    [Fact]
    public void APublicMethodOfTheValue_IsCalled_WithArgumentsConverted()
    {
        Value("header.o.Greet('ann')").Should().Be("hi ann");
        Value("header.o.Twice(21)").Should().Be(42L, "the int literal converts to the long parameter");
    }

    [Fact]
    public void ANullValue_GivesNull()
    {
        Value("header.missing.toUpper()").Should().BeNull();
    }

    [Fact]
    public void AMethodThatThrows_IsAPositionedError()
    {
        var act = () => Value("header.o.Boom()");

        act.Should().Throw<ExpressionEvaluationException>()
            .WithMessage("header.o.Boom() at position 1 threw InvalidOperationException: boom")
            .WithInnerException<InvalidOperationException>();
    }

    [Fact]
    public void AMethodTheTypeDoesNotHave_IsAnError()
    {
        var act = () => Value("header.s.nosuch()");

        act.Should().Throw<ExpressionEvaluationException>().WithMessage("'nosuch' is not a method of String at position 1 (in header.s.nosuch()).");
    }

    [Fact]
    public void InABareValueExpression_TheSameError()
    {
        // Step 28b: the bare value path is the AST's too; the older compiler swallowed the exception into null.
        var act = () => ExpressionResolver.GetCompiledValueExpression("header.o.Boom()")(Message());

        act.Should().Throw<ExpressionEvaluationException>().WithMessage("header.o.Boom() at position 1 threw InvalidOperationException: boom");
    }

    [Fact]
    public void TheWrongNumberOfArguments_IsAnError()
    {
        var act = () => Value("header.o.Greet()");

        act.Should().Throw<ExpressionEvaluationException>().WithMessage("'Greet' of MethodTarget takes 1 argument(s), not 0, at position 1 (in header.o.Greet()).");
    }

    [Fact]
    public void UnderTheSandbox_AReflectionCallIsRefused_TheHelpersAreNot()
    {
        using var sandbox = ExpressionSandbox.Enter();

        Value("header.s.toUpper()").Should().Be("HELLO WORLD");
        var act = () => Value("header.o.Greet('ann')");
        act.Should().Throw<ExpressionSandboxViolationException>();
    }
}
