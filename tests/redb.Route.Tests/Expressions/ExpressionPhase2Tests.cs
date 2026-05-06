using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Expressions.Ast;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// Tests for Expression Language Phase 2 features:
/// null-coalescing (??), ternary (? :), concat() function, and index access ([n]).
/// </summary>
[Collection("ExpressionResolver")]
public class ExpressionPhase2Tests : IDisposable
{
    public ExpressionPhase2Tests()
    {
        ExpressionResolver.ClearAllCaches();
    }

    public void Dispose()
    {
        ExpressionResolver.ClearAllCaches();
    }

    private static IExchange CreateExchange(object? body = null)
        => new Exchange(new Message(body));

    // ═══════════════════════════════════════════════════
    // NULL-COALESCING (??)
    // ═══════════════════════════════════════════════════

    #region Null-coalescing (??)

    [Fact]
    public void NullCoalescing_LeftNotNull_ReturnsLeft()
    {
        var exchange = CreateExchange();
        exchange.Properties["name"] = "Alice";

        var result = ExpressionResolver.ResolveExpression("name ?? 'default'", exchange);
        result.Should().Be("Alice");
    }

    [Fact]
    public void NullCoalescing_LeftNull_ReturnsRight()
    {
        var exchange = CreateExchange();

        var result = ExpressionResolver.ResolveExpression("missing ?? 'fallback'", exchange);
        result.Should().Be("fallback");
    }

    [Fact]
    public void NullCoalescing_ChainedMultiple()
    {
        var exchange = CreateExchange();
        exchange.Properties["c"] = "third";

        var result = ExpressionResolver.ResolveExpression("a ?? b ?? c", exchange);
        result.Should().Be("third");
    }

    [Fact]
    public void NullCoalescing_WithNumericFallback()
    {
        var exchange = CreateExchange();

        var result = ExpressionResolver.ResolveExpression("count ?? 0", exchange);
        result.Should().Be(0);
    }

    [Fact]
    public void NullCoalescing_InTemplate_LeftPresent()
    {
        var exchange = CreateExchange();
        exchange.Properties["name"] = "Bob";

        var result = ExpressionResolver.ProcessTemplate("Hello ${name ?? 'World'}!", exchange);
        result.Should().Be("Hello Bob!");
    }

    [Fact]
    public void NullCoalescing_InTemplate_LeftMissing()
    {
        var exchange = CreateExchange();

        var result = ExpressionResolver.ProcessTemplate("Hello ${name ?? 'World'}!", exchange);
        result.Should().Be("Hello World!");
    }

    [Fact]
    public void NullCoalescing_BothNotNull_ReturnsLeft()
    {
        var exchange = CreateExchange();
        exchange.Properties["a"] = "first";
        exchange.Properties["b"] = "second";

        var result = ExpressionResolver.ResolveExpression("a ?? b", exchange);
        result.Should().Be("first");
    }

    [Fact]
    public void NullCoalescing_NullFallthroughToLiteral()
    {
        var exchange = CreateExchange();

        var result = ExpressionResolver.ResolveExpression("x ?? 'default_value'", exchange);
        result.Should().Be("default_value");
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // TERNARY (? :)
    // ═══════════════════════════════════════════════════

    #region Ternary (? :)

    [Fact]
    public void Ternary_TrueCondition_ReturnsIfTrue()
    {
        var exchange = CreateExchange();
        exchange.Properties["flag"] = true;

        var result = ExpressionResolver.ResolveExpression("flag ? 'yes' : 'no'", exchange);
        result.Should().Be("yes");
    }

    [Fact]
    public void Ternary_FalseCondition_ReturnsIfFalse()
    {
        var exchange = CreateExchange();
        exchange.Properties["flag"] = false;

        var result = ExpressionResolver.ResolveExpression("flag ? 'yes' : 'no'", exchange);
        result.Should().Be("no");
    }

    [Fact]
    public void Ternary_NullCondition_ReturnsIfFalse()
    {
        var exchange = CreateExchange();

        var result = ExpressionResolver.ResolveExpression("missing ? 'found' : 'not found'", exchange);
        result.Should().Be("not found");
    }

    [Fact]
    public void Ternary_NumericCondition_NonZeroIsTrue()
    {
        var exchange = CreateExchange();
        exchange.Properties["count"] = 5;

        var result = ExpressionResolver.ResolveExpression("count ? 'has items' : 'empty'", exchange);
        result.Should().Be("has items");
    }

    [Fact]
    public void Ternary_ComparisonCondition()
    {
        var exchange = CreateExchange();
        exchange.Properties["age"] = 25;

        var result = ExpressionResolver.ResolveExpression("age > 18 ? 'adult' : 'minor'", exchange);
        result.Should().Be("adult");
    }

    [Fact]
    public void Ternary_WithExpressionBranches()
    {
        var exchange = CreateExchange();
        exchange.Properties["x"] = 10;
        exchange.Properties["y"] = 20;
        exchange.Properties["useX"] = true;

        var result = ExpressionResolver.ResolveExpression("useX ? x : y", exchange);
        result.Should().Be(10);
    }

    [Fact]
    public void Ternary_InTemplate()
    {
        var exchange = CreateExchange();
        exchange.Properties["isVip"] = true;

        var result = ExpressionResolver.ProcessTemplate("Status: ${isVip ? 'VIP' : 'Regular'}", exchange);
        result.Should().Be("Status: VIP");
    }

    [Fact]
    public void Ternary_InTemplate_FalsePath()
    {
        var exchange = CreateExchange();
        exchange.Properties["isVip"] = false;

        var result = ExpressionResolver.ProcessTemplate("Status: ${isVip ? 'VIP' : 'Regular'}", exchange);
        result.Should().Be("Status: Regular");
    }

    [Fact]
    public void Ternary_NestedTernary()
    {
        var exchange = CreateExchange();
        exchange.Properties["level"] = 3;

        // level > 2 ? 'high' : (level > 1 ? 'medium' : 'low')
        var result = ExpressionResolver.ResolveExpression("level > 2 ? 'high' : level > 1 ? 'medium' : 'low'", exchange);
        result.Should().Be("high");
    }

    [Fact]
    public void Ternary_StringCondition_NonEmptyIsTrue()
    {
        var exchange = CreateExchange();
        exchange.Properties["name"] = "Alice";

        var result = ExpressionResolver.ResolveExpression("name ? 'named' : 'anonymous'", exchange);
        result.Should().Be("named");
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // CONCAT() FUNCTION
    // ═══════════════════════════════════════════════════

    #region concat() function

    [Fact]
    public void Concat_TwoStrings()
    {
        var exchange = CreateExchange();

        var result = ExpressionResolver.ResolveExpression("concat('Hello', ' World')", exchange);
        result.Should().Be("Hello World");
    }

    [Fact]
    public void Concat_MultipleArguments()
    {
        var exchange = CreateExchange();

        var result = ExpressionResolver.ResolveExpression("concat('a', 'b', 'c', 'd')", exchange);
        result.Should().Be("abcd");
    }

    [Fact]
    public void Concat_WithProperties()
    {
        var exchange = CreateExchange();
        exchange.Properties["first"] = "John";
        exchange.Properties["last"] = "Doe";

        var result = ExpressionResolver.ResolveExpression("concat(first, ' ', last)", exchange);
        result.Should().Be("John Doe");
    }

    [Fact]
    public void Concat_WithNulls()
    {
        var exchange = CreateExchange();
        exchange.Properties["name"] = "Alice";

        var result = ExpressionResolver.ResolveExpression("concat(name, missing, '!')", exchange);
        result.Should().Be("Alice!");
    }

    [Fact]
    public void Concat_WithNumbers()
    {
        var exchange = CreateExchange();
        exchange.Properties["count"] = 42;

        var result = ExpressionResolver.ResolveExpression("concat('Count: ', count)", exchange);
        result.Should().Be("Count: 42");
    }

    [Fact]
    public void Concat_InTemplate()
    {
        var exchange = CreateExchange();
        exchange.Properties["first"] = "Jane";
        exchange.Properties["last"] = "Smith";

        var result = ExpressionResolver.ProcessTemplate("Name: ${concat(first, ' ', last)}", exchange);
        result.Should().Be("Name: Jane Smith");
    }

    [Fact]
    public void Concat_SingleArgument()
    {
        var exchange = CreateExchange();

        var result = ExpressionResolver.ResolveExpression("concat('solo')", exchange);
        result.Should().Be("solo");
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // INDEX ACCESS ([n])
    // ═══════════════════════════════════════════════════

    #region Index access ([n])

    [Fact]
    public void IndexAccess_ListByIndex()
    {
        var exchange = CreateExchange();
        exchange.Properties["items"] = new List<string> { "alpha", "beta", "gamma" };

        var result = ExpressionResolver.ResolveExpression("items[0]", exchange);
        result.Should().Be("alpha");
    }

    [Fact]
    public void IndexAccess_ListByIndex_SecondElement()
    {
        var exchange = CreateExchange();
        exchange.Properties["items"] = new List<string> { "alpha", "beta", "gamma" };

        var result = ExpressionResolver.ResolveExpression("items[1]", exchange);
        result.Should().Be("beta");
    }

    [Fact]
    public void IndexAccess_ListByIndex_LastElement()
    {
        var exchange = CreateExchange();
        exchange.Properties["items"] = new List<string> { "alpha", "beta", "gamma" };

        var result = ExpressionResolver.ResolveExpression("items[2]", exchange);
        result.Should().Be("gamma");
    }

    [Fact]
    public void IndexAccess_OutOfBounds_ReturnsNull()
    {
        var exchange = CreateExchange();
        exchange.Properties["items"] = new List<string> { "alpha" };

        var result = ExpressionResolver.ResolveExpression("items[5]", exchange);
        result.Should().BeNull();
    }

    [Fact]
    public void IndexAccess_ArrayType()
    {
        var exchange = CreateExchange();
        exchange.Properties["arr"] = new[] { 10, 20, 30 };

        var result = ExpressionResolver.ResolveExpression("arr[1]", exchange);
        result.Should().Be(20);
    }

    [Fact]
    public void IndexAccess_DictionaryByStringKey()
    {
        var exchange = CreateExchange();
        exchange.Properties["dict"] = new Dictionary<string, object>
        {
            ["name"] = "Alice",
            ["age"] = 30
        };

        var result = ExpressionResolver.ResolveExpression("dict['name']", exchange);
        result.Should().Be("Alice");
    }

    [Fact]
    public void IndexAccess_InTemplate()
    {
        var exchange = CreateExchange();
        exchange.Properties["colors"] = new List<string> { "red", "green", "blue" };

        var result = ExpressionResolver.ProcessTemplate("First: ${colors[0]}", exchange);
        result.Should().Be("First: red");
    }

    [Fact]
    public void IndexAccess_ChainedWithPropertyAccess()
    {
        var exchange = CreateExchange();
        exchange.Properties["users"] = new List<object>
        {
            new { Name = "Alice", Age = 30 },
            new { Name = "Bob", Age = 25 }
        };

        var result = ExpressionResolver.ResolveExpression("users[0].Name", exchange);
        result.Should().Be("Alice");
    }

    [Fact]
    public void IndexAccess_NullObject_ReturnsNull()
    {
        var exchange = CreateExchange();

        var result = ExpressionResolver.ResolveExpression("missing[0]", exchange);
        result.Should().BeNull();
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // AST PARSING
    // ═══════════════════════════════════════════════════

    #region AST parsing

    [Fact]
    public void Ast_NullCoalescing_Parsed()
    {
        var tokenizer = new Tokenizer("a ?? b");
        var tokens = tokenizer.GetAllTokens();
        var parser = new Parser(tokens);
        var ast = parser.Parse();

        ast.Should().BeOfType<BinaryOperationNode>();
        var bin = (BinaryOperationNode)ast;
        bin.Operator.Should().Be("??");
        bin.Left.Should().BeOfType<IdentifierNode>();
        bin.Right.Should().BeOfType<IdentifierNode>();
    }

    [Fact]
    public void Ast_Ternary_Parsed()
    {
        var tokenizer = new Tokenizer("a ? b : c");
        var tokens = tokenizer.GetAllTokens();
        var parser = new Parser(tokens);
        var ast = parser.Parse();

        ast.Should().BeOfType<TernaryNode>();
        var tern = (TernaryNode)ast;
        tern.Condition.Should().BeOfType<IdentifierNode>();
        tern.IfTrue.Should().BeOfType<IdentifierNode>();
        tern.IfFalse.Should().BeOfType<IdentifierNode>();
    }

    [Fact]
    public void Ast_Concat_Parsed()
    {
        var tokenizer = new Tokenizer("concat('a', 'b')");
        var tokens = tokenizer.GetAllTokens();
        var parser = new Parser(tokens);
        var ast = parser.Parse();

        ast.Should().BeOfType<FunctionCallNode>();
        var func = (FunctionCallNode)ast;
        func.Name.Should().Be("concat");
        func.Arguments.Should().HaveCount(2);
    }

    [Fact]
    public void Ast_IndexAccess_Parsed()
    {
        var tokenizer = new Tokenizer("items[0]");
        var tokens = tokenizer.GetAllTokens();
        var parser = new Parser(tokens);
        var ast = parser.Parse();

        ast.Should().BeOfType<IndexAccessNode>();
        var idx = (IndexAccessNode)ast;
        idx.Object.Should().BeOfType<IdentifierNode>();
        idx.Index.Should().BeOfType<LiteralNode>();
    }

    [Fact]
    public void Ast_ChainedPropertyAndIndex()
    {
        var tokenizer = new Tokenizer("obj.items[0].name");
        var tokens = tokenizer.GetAllTokens();
        var parser = new Parser(tokens);
        var ast = parser.Parse();

        // Should be PropertyAccessNode(IndexAccessNode(PropertyAccessNode(Identifier, "items"), 0), "name")
        ast.Should().BeOfType<PropertyAccessNode>();
        var prop = (PropertyAccessNode)ast;
        prop.PropertyName.Should().Be("name");
        prop.Object.Should().BeOfType<IndexAccessNode>();
    }

    [Fact]
    public void Ast_TernaryWithComparison()
    {
        var tokenizer = new Tokenizer("x > 0 ? 'positive' : 'non-positive'");
        var tokens = tokenizer.GetAllTokens();
        var parser = new Parser(tokens);
        var ast = parser.Parse();

        ast.Should().BeOfType<TernaryNode>();
        var tern = (TernaryNode)ast;
        tern.Condition.Should().BeOfType<BinaryOperationNode>();
        var cond = (BinaryOperationNode)tern.Condition;
        cond.Operator.Should().Be(">");
    }

    [Fact]
    public void Ast_NullCoalescingWithTernary()
    {
        // a ?? (b ? c : d) — ternary has lower precedence than ??
        var tokenizer = new Tokenizer("a ?? b ? 'yes' : 'no'");
        var tokens = tokenizer.GetAllTokens();
        var parser = new Parser(tokens);
        var ast = parser.Parse();

        // Ternary is lowest precedence, so it wraps the ?? 
        ast.Should().BeOfType<TernaryNode>();
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // COMBINED FEATURES
    // ═══════════════════════════════════════════════════

    #region Combined features

    [Fact]
    public void Combined_TernaryWithNullCoalescing()
    {
        var exchange = CreateExchange();
        exchange.Properties["value"] = null;

        // null ? 'yes' : 'no' → false path since null is falsy
        var result = ExpressionResolver.ResolveExpression("value ? 'present' : 'absent'", exchange);
        result.Should().Be("absent");
    }

    [Fact]
    public void Combined_IndexAndTernary()
    {
        var exchange = CreateExchange();
        exchange.Properties["items"] = new List<int> { 1, 2, 3 };

        var result = ExpressionResolver.ResolveExpression("items[0] > 0 ? 'positive' : 'negative'", exchange);
        result.Should().Be("positive");
    }

    [Fact]
    public void Combined_ConcatAndTernary()
    {
        var exchange = CreateExchange();
        exchange.Properties["isAdmin"] = true;
        exchange.Properties["name"] = "Alice";

        var result = ExpressionResolver.ResolveExpression(
            "isAdmin ? concat(name, ' (Admin)') : name", exchange);
        result.Should().Be("Alice (Admin)");
    }

    [Fact]
    public void Combined_AllFeaturesInTemplate()
    {
        var exchange = CreateExchange();
        exchange.Properties["items"] = new List<string> { "first", "second" };
        exchange.Properties["label"] = null;

        var result = ExpressionResolver.ProcessTemplate(
            "Item: ${items[0]}, Label: ${label ?? 'none'}", exchange);
        result.Should().Be("Item: first, Label: none");
    }

    #endregion
}
