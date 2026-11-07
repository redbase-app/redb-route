using AwesomeAssertions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Expressions.Ast;
using redb.Route.Predicates;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// <c>env:NAME</c> (findings register row 34 point 1, closing row 35): the OS environment variable, as Apache Camel's
/// <c>${env:NAME}</c>; <c>null</c> when it is not set; read on every evaluation; refused under the template sandbox.
/// Until 4.7.0 the engine rendered <c>${env:NAME}</c> empty, so an HTTP <c>AuthToken = "${env:BILLING_TOKEN}"</c> sent an
/// empty bearer token.
/// </summary>
public sealed class EnvNamespaceTests : IDisposable
{
    private readonly string _name = "REDB_ENV_TEST_" + Guid.NewGuid().ToString("N");

    public EnvNamespaceTests() => Environment.SetEnvironmentVariable(_name, "from-env");

    public void Dispose() => Environment.SetEnvironmentVariable(_name, null);

    private static Exchange Message() => new(new Message("x"));

    [Fact]
    public void AWholePlaceholder_IsTheValue()
    {
        new StringExpression("${env:" + _name + "}").Evaluate<object>(Message()).Should().Be("from-env");
    }

    [Fact]
    public void InText_TheValueIsRendered()
    {
        ExpressionResolver.ProcessTemplate("Bearer ${env:" + _name + "}", Message()).Should().Be("Bearer from-env");
    }

    [Fact]
    public void AMissingVariable_IsNull_AndRendersEmpty()
    {
        var missing = "REDB_ENV_MISSING_" + Guid.NewGuid().ToString("N");

        new StringExpression("${env:" + missing + "}").Evaluate<object>(Message()).Should().BeNull();
        ExpressionResolver.ProcessTemplate("[${env:" + missing + "}]", Message()).Should().Be("[]");
    }

    [Fact]
    public void InACondition()
    {
        PredicateFactory.FromString("env:" + _name + " == 'from-env'").Matches(Message()).Should().BeTrue();
    }

    [Fact]
    public void IsReadOnEveryEvaluation()
    {
        var expression = new StringExpression("${env:" + _name + "}");
        expression.Evaluate<object>(Message()).Should().Be("from-env");

        Environment.SetEnvironmentVariable(_name, "changed");

        expression.Evaluate<object>(Message()).Should().Be("changed");
    }

    [Fact]
    public void UnderTheSandbox_IsRefused()
    {
        var expression = new StringExpression("${env:" + _name + "}");
        using var sandbox = ExpressionSandbox.Enter();

        var act = () => expression.Evaluate<object>(Message());

        act.Should().Throw<ExpressionSandboxViolationException>().WithMessage($"'env:{_name}' cannot be read here: the expression runs in a sandbox*");
    }

    [Fact]
    public void AParenthesis_IsAnError()
    {
        var act = () => new Parser(new Tokenizer("env:PATH()").GetAllTokens()).Parse();

        act.Should().Throw<InvalidOperationException>().WithMessage("env:PATH names an environment variable and calls nothing (position 1 within the expression).");
    }

    [Fact]
    public void Ternary_WithSpaces_ReadsAsBefore()
    {
        new Tokenizer("flag ? env : other").GetAllTokens().Where(t => t.Type != TokenType.Eof).Select(t => t.Type)
            .Should().Equal(TokenType.Identifier, TokenType.Operator, TokenType.Identifier, TokenType.Colon, TokenType.Identifier);
    }

    [Fact]
    public void AnHttpStyleTokenExpression_NowCarriesTheValue()
    {
        // What HttpConnectionFactory.AuthToken does with a ${...} value: a StringExpression resolved per request.
        new StringExpression("${env:" + _name + "}").Evaluate<string>(Message()).Should().Be("from-env");
    }
}
