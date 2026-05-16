using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// Tests for Expression Language Phase 3 features:
/// Built-in functions (upper, lower, trim, length, substring, abs, round, min, max),
/// word-form logical operators (NOT, AND, OR, XOR), and AST routing.
/// </summary>
[Collection("ExpressionResolver")]
public class ExpressionPhase3Tests : IDisposable
{
    public ExpressionPhase3Tests()
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
    // UPPER()
    // ═══════════════════════════════════════════════════

    #region upper()

    [Fact]
    public void Upper_StringLiteral()
    {
        var exchange = CreateExchange();
        var result = ExpressionResolver.ResolveExpression("upper('hello')", exchange);
        result.Should().Be("HELLO");
    }

    [Fact]
    public void Upper_Property()
    {
        var exchange = CreateExchange();
        exchange.Properties["name"] = "alice";
        var result = ExpressionResolver.ResolveExpression("upper(property.name)", exchange);
        result.Should().Be("ALICE");
    }

    [Fact]
    public void Upper_NullProperty_ReturnsNull()
    {
        var exchange = CreateExchange();
        var result = ExpressionResolver.ResolveExpression("upper(property.missing)", exchange);
        result.Should().BeNull();
    }

    [Fact]
    public void Upper_InComparison()
    {
        var exchange = CreateExchange();
        exchange.Properties["name"] = "alice";
        var result = ExpressionResolver.ResolveExpression("upper(property.name) == 'ALICE'", exchange);
        result.Should().Be(true);
    }

    [Fact]
    public void Upper_Template()
    {
        var exchange = CreateExchange();
        exchange.Properties["name"] = "alice";
        var result = ExpressionResolver.ResolveExpression("Hello ${upper(property.name)}!", exchange);
        result.Should().Be("Hello ALICE!");
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // LOWER()
    // ═══════════════════════════════════════════════════

    #region lower()

    [Fact]
    public void Lower_StringLiteral()
    {
        var exchange = CreateExchange();
        var result = ExpressionResolver.ResolveExpression("lower('HELLO')", exchange);
        result.Should().Be("hello");
    }

    [Fact]
    public void Lower_Property()
    {
        var exchange = CreateExchange();
        exchange.Properties["name"] = "ALICE";
        var result = ExpressionResolver.ResolveExpression("lower(property.name)", exchange);
        result.Should().Be("alice");
    }

    [Fact]
    public void Lower_NullProperty_ReturnsNull()
    {
        var exchange = CreateExchange();
        var result = ExpressionResolver.ResolveExpression("lower(property.missing)", exchange);
        result.Should().BeNull();
    }

    [Fact]
    public void Lower_InComparison()
    {
        var exchange = CreateExchange();
        exchange.Properties["name"] = "ALICE";
        var result = ExpressionResolver.ResolveExpression("lower(property.name) == 'alice'", exchange);
        result.Should().Be(true);
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // TRIM()
    // ═══════════════════════════════════════════════════

    #region trim()

    [Fact]
    public void Trim_StringLiteral()
    {
        var exchange = CreateExchange();
        var result = ExpressionResolver.ResolveExpression("trim('  hello  ')", exchange);
        result.Should().Be("hello");
    }

    [Fact]
    public void Trim_Property()
    {
        var exchange = CreateExchange();
        exchange.Properties["name"] = "  alice  ";
        var result = ExpressionResolver.ResolveExpression("trim(property.name)", exchange);
        result.Should().Be("alice");
    }

    [Fact]
    public void Trim_NullProperty_ReturnsNull()
    {
        var exchange = CreateExchange();
        var result = ExpressionResolver.ResolveExpression("trim(property.missing)", exchange);
        result.Should().BeNull();
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // LENGTH()
    // ═══════════════════════════════════════════════════

    #region length()

    [Fact]
    public void Length_StringLiteral()
    {
        var exchange = CreateExchange();
        var result = ExpressionResolver.ResolveExpression("length('hello')", exchange);
        result.Should().Be(5);
    }

    [Fact]
    public void Length_Property()
    {
        var exchange = CreateExchange();
        exchange.Properties["name"] = "alice";
        var result = ExpressionResolver.ResolveExpression("length(property.name)", exchange);
        result.Should().Be(5);
    }

    [Fact]
    public void Length_NullProperty_ReturnsNull()
    {
        var exchange = CreateExchange();
        var result = ExpressionResolver.ResolveExpression("length(property.missing)", exchange);
        result.Should().BeNull();
    }

    [Fact]
    public void Length_InComparison()
    {
        var exchange = CreateExchange();
        exchange.Properties["name"] = "alice";
        var result = ExpressionResolver.ResolveExpression("length(property.name) > 3", exchange);
        result.Should().Be(true);
    }

    [Fact]
    public void Length_EmptyString()
    {
        var exchange = CreateExchange();
        exchange.Properties["text"] = "";
        var result = ExpressionResolver.ResolveExpression("length(property.text)", exchange);
        result.Should().Be(0);
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // SUBSTRING()
    // ═══════════════════════════════════════════════════

    #region substring()

    [Fact]
    public void Substring_TwoArgs()
    {
        var exchange = CreateExchange();
        var result = ExpressionResolver.ResolveExpression("substring('hello world', 6)", exchange);
        result.Should().Be("world");
    }

    [Fact]
    public void Substring_ThreeArgs()
    {
        var exchange = CreateExchange();
        var result = ExpressionResolver.ResolveExpression("substring('hello world', 0, 5)", exchange);
        result.Should().Be("hello");
    }

    [Fact]
    public void Substring_Property()
    {
        var exchange = CreateExchange();
        exchange.Properties["text"] = "abcdef";
        var result = ExpressionResolver.ResolveExpression("substring(property.text, 2, 3)", exchange);
        result.Should().Be("cde");
    }

    [Fact]
    public void Substring_NullProperty_ReturnsNull()
    {
        var exchange = CreateExchange();
        var result = ExpressionResolver.ResolveExpression("substring(property.missing, 0, 3)", exchange);
        result.Should().BeNull();
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // ABS()
    // ═══════════════════════════════════════════════════

    #region abs()

    [Fact]
    public void Abs_Negative()
    {
        var exchange = CreateExchange();
        exchange.Properties["val"] = -42;
        var result = ExpressionResolver.ResolveExpression("abs(property.val)", exchange);
        Convert.ToDouble(result).Should().Be(42);
    }

    [Fact]
    public void Abs_Positive()
    {
        var exchange = CreateExchange();
        exchange.Properties["val"] = 42;
        var result = ExpressionResolver.ResolveExpression("abs(property.val)", exchange);
        Convert.ToDouble(result).Should().Be(42);
    }

    [Fact]
    public void Abs_NullProperty_ReturnsNull()
    {
        var exchange = CreateExchange();
        var result = ExpressionResolver.ResolveExpression("abs(property.missing)", exchange);
        result.Should().BeNull();
    }

    [Fact]
    public void Abs_StringNumber()
    {
        var exchange = CreateExchange();
        exchange.Properties["val"] = "-7.5";
        var result = ExpressionResolver.ResolveExpression("abs(property.val)", exchange);
        Convert.ToDouble(result).Should().Be(7.5);
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // ROUND()
    // ═══════════════════════════════════════════════════

    #region round()

    [Fact]
    public void Round_OneArg()
    {
        var exchange = CreateExchange();
        exchange.Properties["val"] = 3.7;
        var result = ExpressionResolver.ResolveExpression("round(property.val)", exchange);
        Convert.ToDouble(result).Should().Be(4);
    }

    [Fact]
    public void Round_TwoArgs()
    {
        var exchange = CreateExchange();
        exchange.Properties["val"] = 3.14159;
        var result = ExpressionResolver.ResolveExpression("round(property.val, 2)", exchange);
        Convert.ToDouble(result).Should().Be(3.14);
    }

    [Fact]
    public void Round_NullProperty_ReturnsNull()
    {
        var exchange = CreateExchange();
        var result = ExpressionResolver.ResolveExpression("round(property.missing)", exchange);
        result.Should().BeNull();
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // MIN() / MAX()
    // ═══════════════════════════════════════════════════

    #region min() / max()

    [Fact]
    public void Min_TwoValues()
    {
        var exchange = CreateExchange();
        exchange.Properties["a"] = 10;
        exchange.Properties["b"] = 20;
        var result = ExpressionResolver.ResolveExpression("min(property.a, property.b)", exchange);
        Convert.ToDouble(result).Should().Be(10);
    }

    [Fact]
    public void Min_TwoLiterals()
    {
        var exchange = CreateExchange();
        var result = ExpressionResolver.ResolveExpression("min(5, 3)", exchange);
        Convert.ToDouble(result).Should().Be(3);
    }

    [Fact]
    public void Max_TwoValues()
    {
        var exchange = CreateExchange();
        exchange.Properties["a"] = 10;
        exchange.Properties["b"] = 20;
        var result = ExpressionResolver.ResolveExpression("max(property.a, property.b)", exchange);
        Convert.ToDouble(result).Should().Be(20);
    }

    [Fact]
    public void Max_TwoLiterals()
    {
        var exchange = CreateExchange();
        var result = ExpressionResolver.ResolveExpression("max(7, 3)", exchange);
        Convert.ToDouble(result).Should().Be(7);
    }

    [Fact]
    public void Min_NullProperty_ReturnsNull()
    {
        var exchange = CreateExchange();
        exchange.Properties["a"] = 10;
        var result = ExpressionResolver.ResolveExpression("min(property.a, property.missing)", exchange);
        result.Should().BeNull();
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // COMBINED EXPRESSIONS
    // ═══════════════════════════════════════════════════

    #region Combined expressions

    [Fact]
    public void Upper_Lower_Nested_Concat()
    {
        var exchange = CreateExchange();
        exchange.Properties["first"] = "alice";
        exchange.Properties["last"] = "SMITH";
        var result = ExpressionResolver.ResolveExpression(
            "concat(upper(property.first), ' ', lower(property.last))", exchange);
        result.Should().Be("ALICE smith");
    }

    [Fact]
    public void Length_WithArithmetic()
    {
        var exchange = CreateExchange();
        exchange.Properties["name"] = "alice";
        var result = ExpressionResolver.ResolveExpression("length(property.name) + 10", exchange);
        Convert.ToDouble(result).Should().Be(15);
    }

    [Fact]
    public void Trim_Upper_Chained()
    {
        var exchange = CreateExchange();
        exchange.Properties["name"] = "  alice  ";
        var result = ExpressionResolver.ResolveExpression("upper(trim(property.name))", exchange);
        result.Should().Be("ALICE");
    }

    [Fact]
    public void Abs_InComparison()
    {
        var exchange = CreateExchange();
        exchange.Properties["val"] = -15;
        var result = ExpressionResolver.ResolveExpression("abs(property.val) > 10", exchange);
        result.Should().Be(true);
    }

    [Fact]
    public void Round_InArithmetic()
    {
        var exchange = CreateExchange();
        exchange.Properties["val"] = 3.7;
        var result = ExpressionResolver.ResolveExpression("round(property.val) * 2", exchange);
        Convert.ToDouble(result).Should().Be(8);
    }

    [Fact]
    public void Upper_WithNullCoalescing()
    {
        var exchange = CreateExchange();
        var result = ExpressionResolver.ResolveExpression("upper(property.missing ?? 'default')", exchange);
        result.Should().Be("DEFAULT");
    }

    [Fact]
    public void Length_WithTernary()
    {
        var exchange = CreateExchange();
        exchange.Properties["name"] = "alice";
        var result = ExpressionResolver.ResolveExpression(
            "length(property.name) > 3 ? 'long' : 'short'", exchange);
        result.Should().Be("long");
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // WORD-FORM LOGICAL OPERATORS (NOT, AND, OR, XOR)
    // ═══════════════════════════════════════════════════

    #region Word-form logical operators

    [Fact]
    public void NOT_TrueProperty_ReturnsFalse()
    {
        var exchange = CreateExchange();
        exchange.Properties["flag"] = true;
        var result = ExpressionResolver.ResolveExpression("NOT property.flag", exchange);
        result.Should().Be(false);
    }

    [Fact]
    public void NOT_FalseProperty_ReturnsTrue()
    {
        var exchange = CreateExchange();
        exchange.Properties["flag"] = false;
        var result = ExpressionResolver.ResolveExpression("NOT property.flag", exchange);
        result.Should().Be(true);
    }

    [Fact]
    public void AND_BothTrue()
    {
        var exchange = CreateExchange();
        exchange.Properties["a"] = 10;
        exchange.Properties["b"] = 20;
        var result = ExpressionResolver.ResolveExpression("property.a > 5 AND property.b > 15", exchange);
        result.Should().Be(true);
    }

    [Fact]
    public void AND_OneFalse()
    {
        var exchange = CreateExchange();
        exchange.Properties["a"] = 10;
        exchange.Properties["b"] = 5;
        var result = ExpressionResolver.ResolveExpression("property.a > 5 AND property.b > 15", exchange);
        result.Should().Be(false);
    }

    [Fact]
    public void OR_OneFalse()
    {
        var exchange = CreateExchange();
        exchange.Properties["a"] = 3;
        exchange.Properties["b"] = 20;
        var result = ExpressionResolver.ResolveExpression("property.a > 5 OR property.b > 15", exchange);
        result.Should().Be(true);
    }

    [Fact]
    public void OR_BothFalse()
    {
        var exchange = CreateExchange();
        exchange.Properties["a"] = 3;
        exchange.Properties["b"] = 5;
        var result = ExpressionResolver.ResolveExpression("property.a > 5 OR property.b > 15", exchange);
        result.Should().Be(false);
    }

    [Fact]
    public void XOR_OnlyOneTrue()
    {
        var exchange = CreateExchange();
        exchange.Properties["a"] = 10;
        exchange.Properties["b"] = 5;
        var result = ExpressionResolver.ResolveExpression("property.a > 5 XOR property.b > 15", exchange);
        result.Should().Be(true);
    }

    [Fact]
    public void XOR_BothTrue_ReturnsFalse()
    {
        var exchange = CreateExchange();
        exchange.Properties["a"] = 10;
        exchange.Properties["b"] = 20;
        var result = ExpressionResolver.ResolveExpression("property.a > 5 XOR property.b > 15", exchange);
        result.Should().Be(false);
    }

    [Fact]
    public void SymbolLogical_AndOr()
    {
        var exchange = CreateExchange();
        exchange.Properties["a"] = 10;
        exchange.Properties["b"] = 20;
        var result = ExpressionResolver.ResolveExpression("property.a > 5 && property.b > 15", exchange);
        result.Should().Be(true);
    }

    [Fact]
    public void SymbolLogical_Not()
    {
        var exchange = CreateExchange();
        exchange.Properties["flag"] = true;
        var result = ExpressionResolver.ResolveExpression("!property.flag", exchange);
        result.Should().Be(false);
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // TEMPLATE INTEGRATION
    // ═══════════════════════════════════════════════════

    #region Template integration

    [Fact]
    public void Template_Upper()
    {
        var exchange = CreateExchange();
        exchange.Properties["name"] = "alice";
        var result = ExpressionResolver.ResolveExpression("Name: ${upper(property.name)}", exchange);
        result.Should().Be("Name: ALICE");
    }

    [Fact]
    public void Template_Lower()
    {
        var exchange = CreateExchange();
        exchange.Properties["name"] = "ALICE";
        var result = ExpressionResolver.ResolveExpression("Name: ${lower(property.name)}", exchange);
        result.Should().Be("Name: alice");
    }

    [Fact]
    public void Template_Trim()
    {
        var exchange = CreateExchange();
        exchange.Properties["name"] = "  alice  ";
        var result = ExpressionResolver.ResolveExpression("Name: ${trim(property.name)}", exchange);
        result.Should().Be("Name: alice");
    }

    [Fact]
    public void Template_Length()
    {
        var exchange = CreateExchange();
        exchange.Properties["name"] = "alice";
        var result = ExpressionResolver.ResolveExpression("Length: ${length(property.name)}", exchange);
        result.Should().Be("Length: 5");
    }

    [Fact]
    public void Template_MultipleFunctions()
    {
        var exchange = CreateExchange();
        exchange.Properties["first"] = "alice";
        exchange.Properties["last"] = "smith";
        var result = ExpressionResolver.ResolveExpression(
            "${upper(property.first)} ${upper(property.last)}", exchange);
        result.Should().Be("ALICE SMITH");
    }

    #endregion
}
