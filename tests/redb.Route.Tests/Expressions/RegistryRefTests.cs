using AwesomeAssertions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Expressions.Ast;
using redb.Route.Predicates;

namespace redb.Route.Tests.Expressions;

/// <summary>A registry object the <c>ref:</c> tests read.</summary>
public sealed class ChatSettings
{
    public string Style { get; set; } = "terse";

    public override string ToString() => "style=" + Style;
}

/// <summary>
/// <c>ref:name</c> of the expression language: the object registered under the whole name (dots and dashes
/// included), as in Apache Camel; nothing is called or read on it; a missing name is an error in the bean: words;
/// refused under the sandbox; looked up once per context; the ternary edge of bean: applies.
/// </summary>
public sealed class RegistryRefTests : IAsyncLifetime
{
    private readonly RouteContext _context = new();
    private readonly ChatSettings _settings = new();

    public Task InitializeAsync()
    {
        _context.AddToRegistry("chat.settings", _settings);
        _context.AddToRegistry("seen-orders", new List<int> { 1, 2 });
        _context.AddToRegistry("limit", 5);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _context.DisposeAsync();

    private Exchange Message(params (string Key, object? Value)[] headers)
    {
        var exchange = new Exchange(new Message("payload")) { Context = _context };
        foreach (var (key, value) in headers)
            exchange.In.Headers[key] = value;
        return exchange;
    }

    private static AstNode Parse(string expression) => new Parser(new Tokenizer(expression).GetAllTokens()).Parse();

    [Theory]
    [InlineData("ref:chat.settings", "chat.settings")]
    [InlineData("ref:seen-orders", "seen-orders")]
    [InlineData("ref:limit-1", "limit-1")]
    public void TheWholeRun_IsTheName(string expression, string name)
    {
        Parse(expression).Should().BeOfType<RegistryRefNode>().Which.Reference.Should().Be(name);
    }

    [Fact]
    public void ASpaceBeforeAMinus_Subtracts()
    {
        new StringExpression("${ref:limit - 1}").Evaluate<object>(Message()).Should().Be(4);
    }

    [Fact]
    public void AWholePlaceholder_KeepsTheObject()
    {
        new StringExpression("${ref:chat.settings}").Evaluate<object>(Message()).Should().BeSameAs(_settings);
    }

    [Fact]
    public void InText_TheObjectIsRendered()
    {
        ExpressionResolver.ProcessTemplate("[${ref:chat.settings}]", Message()).Should().Be("[style=terse]");
    }

    [Theory]
    [InlineData("ref:limit > 3", true)]
    [InlineData("header.n < ref:limit", true)]
    [InlineData("${ref:limit == 6}", false)]
    public void InACondition_BareAndTemplated(string condition, bool expected)
    {
        PredicateFactory.FromString(condition).Matches(Message(("n", 2))).Should().Be(expected);
    }

    [Fact]
    public void AsAnArgumentOfABeanCall()
    {
        _context.AddToRegistry("fmt", new RefFormatter());

        new StringExpression("${bean:fmt.Describe(ref:chat.settings)}").Evaluate<object>(Message()).Should().Be("terse");
    }

    [Fact]
    public void AMissingName_NamesIt_InTheBeanWords()
    {
        var act = () => new StringExpression("${ref:absent.thing}").Evaluate<object>(Message());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("ref:absent.thing: 'absent.thing' is not in the context registry. Register it with AddToRegistry or a <bean> declaration.");
    }

    [Fact]
    public void AParenthesis_PointsToBean()
    {
        var act = () => Parse("ref:chat.settings()");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("ref:chat.settings names a registry object and calls nothing; call a method with bean:chat.settings.Method(...) (position 1 within the expression).");
    }

    [Fact]
    public void UnderTheSandbox_IsRefused()
    {
        var expression = new StringExpression("${ref:chat.settings}");
        using var sandbox = ExpressionSandbox.Enter();

        var act = () => expression.Evaluate<object>(Message());

        act.Should().Throw<ExpressionSandboxViolationException>()
            .WithMessage("'ref:chat.settings' cannot be read here: the expression runs in a sandbox*not the registry or the process environment.");
    }

    [Fact]
    public void WithoutAContext_TheErrorSaysWhy()
    {
        var act = () => new StringExpression("${ref:limit}").Evaluate<object>(new Exchange());

        act.Should().Throw<InvalidOperationException>().WithMessage("*has no route context*");
    }

    [Fact]
    public async Task OncePerContext_EachContextItsOwnObject()
    {
        await using var other = new RouteContext();
        other.AddToRegistry("limit", 9);
        var expression = new StringExpression("${ref:limit}");

        expression.Evaluate<object>(Message()).Should().Be(5);
        _context.AddToRegistry("limit", 7);
        expression.Evaluate<object>(Message()).Should().Be(5, "the object is looked up once per context");
        expression.Evaluate<object>(new Exchange(new Message("x")) { Context = other }).Should().Be(9);
    }

    [Fact]
    public void Ternary_WithSpaces_ReadsAsBefore()
    {
        new Tokenizer("flag ? ref : other").GetAllTokens().Where(t => t.Type != TokenType.Eof).Select(t => t.Type)
            .Should().Equal(TokenType.Identifier, TokenType.Operator, TokenType.Identifier, TokenType.Colon, TokenType.Identifier);
    }

    [Fact]
    public void Ternary_WithoutSpaces_BeforeAName_IsARegistryName()
    {
        // The documented edge of bean: holds for ref: as well. Write spaces in a ternary whose branch is named ref.
        new Tokenizer("flag?ref:other").GetAllTokens().Select(t => t.Type).Should().Contain(TokenType.Namespace);
        var act = () => Parse("flag?ref:other");
        act.Should().Throw<InvalidOperationException>().WithMessage("*Colon*");
    }
}

/// <summary>A bean that takes a registry object as an argument.</summary>
public sealed class RefFormatter
{
    public string Describe(ChatSettings settings) => settings.Style;
}
