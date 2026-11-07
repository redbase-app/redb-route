using AwesomeAssertions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Expressions.Ast;
using redb.Route.Predicates;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// The grammar of a namespaced call, <c>bean:name.Method(args)</c> (stage 2 step c): the namespace token, the target
/// with dashed registry names, the required method, <c>${...}</c> in the arguments only, the ternary edge, both
/// dispatchers reaching the AST.
/// </summary>
public sealed class NamespacedCallGrammarTests
{
    private static (TokenType, string)[] Tokens(string expression)
        => [.. new Tokenizer(expression).GetAllTokens().Where(t => t.Type != TokenType.Eof).Select(t => (t.Type, t.Value))];

    private static AstNode Parse(string expression) => new Parser(new Tokenizer(expression).GetAllTokens()).Parse();

    [Fact]
    public void Namespace_IsOneToken_AndTheTargetFollows()
    {
        var tokens = Tokens("bean:svc.Check(body)");

        tokens.Select(t => t.Item1).Should().Equal(
            TokenType.Namespace, TokenType.Function, TokenType.LeftParen, TokenType.Identifier, TokenType.RightParen);
        tokens[0].Item2.Should().Be("bean");
        tokens[1].Item2.Should().Be("svc.Check");
    }

    [Fact]
    public void DashedRegistryName_IsPartOfTheTarget()
    {
        var call = Parse("bean:seen-orders.Check()").Should().BeOfType<BeanCallNode>().Subject;

        call.Reference.Should().Be("seen-orders");
        call.Method.Should().Be("Check");
        call.Arguments.Should().BeEmpty();
    }

    [Fact]
    public void MinusAfterTheCall_IsTheOperator()
    {
        Tokens("bean:svc.Count()-1").Select(t => t.Item1).Should().Equal(
            TokenType.Namespace, TokenType.Function, TokenType.LeftParen, TokenType.RightParen,
            TokenType.Operator, TokenType.Number);
        // The one shape a reader may misread: without parentheses the method ends at the dash.
        Tokens("bean:svc.Count-1").Should().Equal(
            (TokenType.Namespace, "bean"), (TokenType.Identifier, "svc.Count"), (TokenType.Operator, "-"), (TokenType.Number, "1"));
    }

    [Fact]
    public void MethodWithoutParentheses_HasNoArguments()
    {
        var call = Parse("bean:svc.Total").Should().BeOfType<BeanCallNode>().Subject;

        call.Reference.Should().Be("svc");
        call.Method.Should().Be("Total");
        call.Arguments.Should().BeEmpty();
    }

    [Theory]
    [InlineData("bean:svc")]
    [InlineData("bean:svc(body)")]
    [InlineData("bean:svc.(body)")]
    public void TheMethod_IsRequired(string expression)
    {
        var act = () => Parse(expression);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("a bean: call names its method: bean:svc.Method(...) (position 1 within the expression).");
    }

    [Fact]
    public void PlaceholderInTheArguments_IsTheTypedValueOfItsExpression()
    {
        var call = Parse("bean:svc.Check(${header.a}, 'x', ${body})").Should().BeOfType<BeanCallNode>().Subject;

        call.Arguments.Should().HaveCount(3);
        call.Arguments[0].Should().BeOfType<IdentifierNode>().Which.Name.Should().Be("header.a");
        call.Arguments[2].Should().BeOfType<IdentifierNode>().Which.Name.Should().Be("body");
    }

    [Fact]
    public void PlaceholderOutsideBeanArguments_IsRefused()
    {
        var act = () => Parse("header.a + ${header.b}");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("${...} inside an expression is supported only in the arguments of a bean: call (position 12 within the expression).");
    }

    [Fact]
    public void Ternary_WithSpaces_ReadsAsBefore()
    {
        Tokens("flag ? bean : other").Select(t => t.Item1).Should().Equal(
            TokenType.Identifier, TokenType.Operator, TokenType.Identifier, TokenType.Colon, TokenType.Identifier);
    }

    [Fact]
    public void Ternary_WithoutSpaces_BeforeAName_IsANamespacedCall()
    {
        // Documented edge: "bean" written against a colon and a name is the namespace. Write spaces in a ternary
        // whose branch is a variable named bean.
        var act = () => Parse("flag?bean:other");

        act.Should().Throw<InvalidOperationException>().WithMessage("a bean: call names its method: bean:other.Method(...)*");
    }

    [Theory]
    [InlineData("${bean:svc.Check(body)}")]
    [InlineData("${bean:svc.Check(${body})}")]
    [InlineData("total: ${bean:pricing-rules.Gross(header.qty, header.price)}")]
    public void TemplateHole_ReachesTheAst(string template)
    {
        // Only the AST knows the namespace: another path would read the hole as a property and give null. The call
        // site's own error, raised on an exchange without a context, proves the AST compiled it.
        var expression = new StringExpression(template);

        expression.Invoking(e => e.Evaluate<object>(new Exchange()))
            .Should().Throw<InvalidOperationException>().WithMessage("*has no route context*");
    }

    [Theory]
    [InlineData("bean:svc.IsGold(body)")]
    [InlineData("bean:svc.IsGold(body) == true")]
    [InlineData("header.vip && bean:svc.IsGold(body)")]
    public void BareExpression_ReachesTheAst_ThroughBothDispatchers(string expression)
    {
        var condition = PredicateFactory.FromString(expression);
        var value = ExpressionResolver.GetCompiledValueExpression(expression);
        var exchange = new Exchange();
        exchange.In.Headers["vip"] = true;

        condition.Invoking(c => c.Matches(exchange))
            .Should().Throw<InvalidOperationException>().WithMessage("*has no route context*");
        value.Invoking(v => v(exchange))
            .Should().Throw<InvalidOperationException>().WithMessage("*has no route context*");
    }

    [Fact]
    public void BeanInsideAQuotedLiteral_IsText()
    {
        new StringExpression("${'bean:svc.Check()'}").Evaluate<string>(new Exchange()).Should().Be("bean:svc.Check()");
    }

    [Fact]
    public void LoneQuoteAtTheEnd_IsAPositionedError()
    {
        var act = () => ExpressionResolver.GetCompiledValueExpressionWithAst("header.a == '");

        act.Should().Throw<ExpressionCompilationException>()
            .WithMessage("*unterminated string literal starting at position 13 within the expression \"header.a == '\".");
    }

    [Fact]
    public void UnterminatedLiteralWithContent_IsAPositionedError()
    {
        // Findings register row 17, decided: it used to be read truncated ('abc as "ab"), silently.
        var act = () => ExpressionResolver.GetCompiledValueExpressionWithAst("header.a == 'abc");

        act.Should().Throw<ExpressionCompilationException>()
            .WithMessage("*unterminated string literal starting at position 13 within the expression \"header.a == 'abc\".");
    }

    [Theory]
    [InlineData("header.s == 'Hello World'")]
    [InlineData("concat('a', \"b\")")]
    [InlineData("header.s == 'it\\'s'")]
    public void ClosedLiterals_ReadAsBefore(string expression)
    {
        var act = () => ExpressionResolver.GetCompiledValueExpressionWithAst(expression);

        act.Should().NotThrow();
    }
}
