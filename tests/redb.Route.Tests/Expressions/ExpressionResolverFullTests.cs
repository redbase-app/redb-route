using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// Comprehensive tests for ExpressionResolver — covers template processing,
/// logical expressions, value expressions, arithmetic, unary/binary operators,
/// increment/decrement, string methods, type coercion, nested properties,
/// runtime resolvers, edge cases, and AST integration.
/// </summary>
[Collection("ExpressionResolver")]
public class ExpressionResolverFullTests : IDisposable
{
    public ExpressionResolverFullTests()
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
    // 1. TEMPLATE RESOLUTION — ProcessTemplate
    // ═══════════════════════════════════════════════════

    #region Template resolution

    [Fact]
    public void ProcessTemplate_EmptyString_ReturnsEmpty()
    {
        var exchange = CreateExchange("body");
        var result = ExpressionResolver.ProcessTemplate("", exchange);
        result.Should().Be("");
    }

    [Fact]
    public void ProcessTemplate_NullTemplate_ReturnsNull()
    {
        var exchange = CreateExchange("body");
        var result = ExpressionResolver.ProcessTemplate(null!, exchange);
        result.Should().BeNull();
    }

    [Fact]
    public void ProcessTemplate_BodyDotProperty_ResolvesNestedProperty()
    {
        var exchange = CreateExchange(new { Name = "Alice", Age = 30 });
        var result = ExpressionResolver.ProcessTemplate("Name: ${body.Name}", exchange);
        result.Should().Be("Name: Alice");
    }

    [Fact]
    public void ProcessTemplate_BodyDotNestedProperty_ResolvesDeepNesting()
    {
        var exchange = CreateExchange(new { Address = new { City = "Moscow" } });
        var result = ExpressionResolver.ProcessTemplate("City: ${body.Address.City}", exchange);
        result.Should().Be("City: Moscow");
    }

    [Fact]
    public void ProcessTemplate_PropertyDotNestedProperty_ResolvesNesting()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["customer"] = new { Name = "Bob", Address = new { City = "SPb" } };
        var result = ExpressionResolver.ProcessTemplate("City: ${property.customer.Address.City}", exchange);
        result.Should().Be("City: SPb");
    }

    [Fact]
    public void ProcessTemplate_ExceptionFullObject_ReturnsToString()
    {
        var exchange = CreateExchange("body");
        exchange.Exception = new InvalidOperationException("fail");
        var result = ExpressionResolver.ProcessTemplate("Err: ${exception}", exchange);
        result.Should().Contain("fail");
    }

    [Fact]
    public void ProcessTemplate_ExceptionStackTrace_WhenNull_ReturnsEmpty()
    {
        var exchange = CreateExchange("body");
        // No exception set
        var result = ExpressionResolver.ProcessTemplate("ST: ${exception.StackTrace}", exchange);
        result.Should().Be("ST: ");
    }

    [Fact]
    public void ProcessTemplate_LogicalFunction_ReturnsTrue()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["x"] = 10;
        var result = ExpressionResolver.ProcessTemplate("Result: ${logical(property.x > 5)}", exchange);
        result.Should().Be("Result: True");
    }

    [Fact]
    public void ProcessTemplate_LogicalFunction_ReturnsFalse()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["x"] = 3;
        var result = ExpressionResolver.ProcessTemplate("Result: ${logical(property.x > 5)}", exchange);
        result.Should().Be("Result: False");
    }

    [Fact]
    public void ProcessTemplate_ArithmeticExpression_InsideTemplate()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["a"] = 10;
        exchange.Properties["b"] = 7;
        var result = ExpressionResolver.ProcessTemplate("Sum: ${property.a + property.b}", exchange);
        result.Should().Be("Sum: 17");
    }

    [Fact]
    public void ProcessTemplate_DefaultExchangeProperty_NoPrefix()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["myVar"] = "hello";
        var result = ExpressionResolver.ProcessTemplate("Value: ${myVar}", exchange);
        result.Should().Be("Value: hello");
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // 2. LOGICAL EXPRESSIONS — EvaluateLogicalExpression
    // ═══════════════════════════════════════════════════

    #region Logical expressions — comparison operators

    [Fact]
    public void LogicalExpression_NotEqual_True()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["status"] = "active";
        ExpressionResolver.EvaluateLogicalExpression("property.status != 'inactive'", exchange)
            .Should().BeTrue();
    }

    [Fact]
    public void LogicalExpression_NotEqual_False()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["status"] = "active";
        ExpressionResolver.EvaluateLogicalExpression("property.status != 'active'", exchange)
            .Should().BeFalse();
    }

    [Fact]
    public void LogicalExpression_LessThan_True()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["count"] = 3;
        ExpressionResolver.EvaluateLogicalExpression("property.count < 10", exchange)
            .Should().BeTrue();
    }

    [Fact]
    public void LogicalExpression_LessThan_False()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["count"] = 10;
        ExpressionResolver.EvaluateLogicalExpression("property.count < 5", exchange)
            .Should().BeFalse();
    }

    [Fact]
    public void LogicalExpression_GreaterOrEqual_True()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["count"] = 5;
        ExpressionResolver.EvaluateLogicalExpression("property.count >= 5", exchange)
            .Should().BeTrue();
    }

    [Fact]
    public void LogicalExpression_GreaterOrEqual_False()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["count"] = 4;
        ExpressionResolver.EvaluateLogicalExpression("property.count >= 5", exchange)
            .Should().BeFalse();
    }

    [Fact]
    public void LogicalExpression_LessOrEqual_True()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["count"] = 5;
        ExpressionResolver.EvaluateLogicalExpression("property.count <= 5", exchange)
            .Should().BeTrue();
    }

    [Fact]
    public void LogicalExpression_LessOrEqual_False()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["count"] = 6;
        ExpressionResolver.EvaluateLogicalExpression("property.count <= 5", exchange)
            .Should().BeFalse();
    }

    #endregion

    #region Logical expressions — connectives

    [Fact]
    public void LogicalExpression_XOR_TrueWhenOnlyOneTrue()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["a"] = 10;
        exchange.Properties["b"] = 3;
        // a > 5 is true, b > 5 is false → XOR = true
        ExpressionResolver.EvaluateLogicalExpression("property.a > 5 XOR property.b > 5", exchange)
            .Should().BeTrue();
    }

    [Fact]
    public void LogicalExpression_XOR_FalseWhenBothTrue()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["a"] = 10;
        exchange.Properties["b"] = 10;
        // Both true → XOR = false
        ExpressionResolver.EvaluateLogicalExpression("property.a > 5 XOR property.b > 5", exchange)
            .Should().BeFalse();
    }

    [Fact]
    public void LogicalExpression_PropertyAsBooleanTruthy()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["flag"] = true;
        ExpressionResolver.EvaluateLogicalExpression("property.flag", exchange)
            .Should().BeTrue();
    }

    [Fact]
    public void LogicalExpression_PropertyAsBooleanFalsy()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["flag"] = false;
        ExpressionResolver.EvaluateLogicalExpression("property.flag", exchange)
            .Should().BeFalse();
    }

    [Fact]
    public void LogicalExpression_StringEquality()
    {
        var exchange = CreateExchange("body");
        exchange.In.Headers["type"] = "order";
        ExpressionResolver.EvaluateLogicalExpression("header.type == 'order'", exchange)
            .Should().BeTrue();
    }

    [Fact]
    public void LogicalExpression_HeaderNumericComparison()
    {
        var exchange = CreateExchange("body");
        exchange.In.Headers["status"] = 200;
        ExpressionResolver.EvaluateLogicalExpression("header.status == 200", exchange)
            .Should().BeTrue();
    }

    [Fact]
    public void LogicalExpression_AND_BothFalse()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["a"] = 1;
        exchange.Properties["b"] = 2;
        ExpressionResolver.EvaluateLogicalExpression("property.a > 5 AND property.b > 5", exchange)
            .Should().BeFalse();
    }

    [Fact]
    public void LogicalExpression_OR_BothFalse()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["a"] = 1;
        exchange.Properties["b"] = 2;
        ExpressionResolver.EvaluateLogicalExpression("property.a > 5 OR property.b > 5", exchange)
            .Should().BeFalse();
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // 3. VALUE EXPRESSIONS — GetCompiledValueExpression
    // ═══════════════════════════════════════════════════

    #region Value expressions — literals

    [Fact]
    public void ValueExpression_DoubleLiteral()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("3.14");
        var exchange = CreateExchange("body");
        Convert.ToDouble(expr(exchange)).Should().BeApproximately(3.14, 0.001);
    }

    [Fact]
    public void ValueExpression_BooleanLiteral_True()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("true");
        var exchange = CreateExchange("body");
        expr(exchange).Should().Be(true);
    }

    [Fact]
    public void ValueExpression_BooleanLiteral_False()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("false");
        var exchange = CreateExchange("body");
        expr(exchange).Should().Be(false);
    }

    [Fact]
    public void ValueExpression_NullLiteral()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("null");
        var exchange = CreateExchange("body");
        expr(exchange).Should().BeNull();
    }

    [Fact]
    public void ValueExpression_DoubleQuotedString()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("\"hello\"");
        var exchange = CreateExchange("body");
        expr(exchange).Should().Be("hello");
    }

    #endregion

    #region Value expressions — nested property access

    [Fact]
    public void ValueExpression_NestedPropertyAccess()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("property.customer.Name");
        var exchange = CreateExchange("body");
        exchange.Properties["customer"] = new { Name = "Alice" };
        expr(exchange)?.ToString().Should().Be("Alice");
    }

    [Fact]
    public void ValueExpression_BodyPropertyAccess()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("body.Name");
        var exchange = CreateExchange(new { Name = "Bob" });
        expr(exchange)?.ToString().Should().Be("Bob");
    }

    [Fact]
    public void ValueExpression_DeepBodyPropertyAccess()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("body.Address.City");
        var exchange = CreateExchange(new { Address = new { City = "Moscow" } });
        expr(exchange)?.ToString().Should().Be("Moscow");
    }

    [Fact]
    public void ValueExpression_PropertyObjectIntField_Compiled()
    {
        // property.MyObject.PropInt — compiled via GetCompiledValueExpression
        var expr = ExpressionResolver.GetCompiledValueExpression("property.MyObject.PropInt");
        var exchange = CreateExchange("body");
        exchange.Properties["MyObject"] = new { PropInt = 42 };
        Convert.ToInt32(expr(exchange)).Should().Be(42);
    }

    [Fact]
    public void ValueExpression_PropertyObjectIntField_Runtime()
    {
        // property.MyObject.PropInt — runtime via ResolveExpression
        var exchange = CreateExchange("body");
        exchange.Properties["MyObject"] = new { PropInt = 99 };
        var result = ExpressionResolver.ResolveExpression("property.MyObject.PropInt", exchange);
        Convert.ToInt32(result).Should().Be(99);
    }

    [Fact]
    public void ValueExpression_HeaderObjectIntField_Compiled()
    {
        // header.MyObject.PropInt — compiled path
        var expr = ExpressionResolver.GetCompiledValueExpression("header.MyObject.PropInt");
        var exchange = CreateExchange("body");
        exchange.In.Headers["MyObject"] = new { PropInt = 77 };
        Convert.ToInt32(expr(exchange)).Should().Be(77);
    }

    [Fact]
    public void ValueExpression_HeaderObjectIntField_Runtime()
    {
        // header.MyObject.PropInt — runtime via ResolveExpression
        var exchange = CreateExchange("body");
        exchange.In.Headers["MyObject"] = new { PropInt = 55 };
        var result = ExpressionResolver.ResolveExpression("header.MyObject.PropInt", exchange);
        Convert.ToInt32(result).Should().Be(55);
    }

    [Fact]
    public void ValueExpression_DeepNestedPropertyAccess_ThreeLevels()
    {
        // property.A.B.C — 3 levels deep
        var expr = ExpressionResolver.GetCompiledValueExpression("property.root.child.value");
        var exchange = CreateExchange("body");
        exchange.Properties["root"] = new { child = new { value = "deep" } };
        expr(exchange)?.ToString().Should().Be("deep");
    }

    [Fact]
    public void ValueExpression_PropertyObjectStringField()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("property.obj.Name");
        var exchange = CreateExchange("body");
        exchange.Properties["obj"] = new { Name = "test", Count = 5 };
        expr(exchange)?.ToString().Should().Be("test");
    }

    [Fact]
    public void ValueExpression_HeaderObjectDeepAccess()
    {
        // header.data.Inner.Value — 2 levels within header object
        var expr = ExpressionResolver.GetCompiledValueExpression("header.data.Inner.Value");
        var exchange = CreateExchange("body");
        exchange.In.Headers["data"] = new { Inner = new { Value = 123 } };
        Convert.ToInt32(expr(exchange)).Should().Be(123);
    }

    [Fact]
    public void ValueExpression_PropertyObjectNullIntermediateReturnsNull()
    {
        // property.obj.Missing — obj exists but Missing doesn't
        var expr = ExpressionResolver.GetCompiledValueExpression("property.obj.Missing");
        var exchange = CreateExchange("body");
        exchange.Properties["obj"] = new { Name = "test" };
        expr(exchange).Should().BeNull();
    }

    [Fact]
    public void ValueExpression_TemplateWithNestedPropertyObject()
    {
        // ${property.MyObject.PropInt} inside a template
        var template = ExpressionResolver.GetCompiledTemplate("Value: ${property.MyObject.PropInt}");
        var exchange = CreateExchange("body");
        exchange.Properties["MyObject"] = new { PropInt = 42 };
        template(exchange).Should().Be("Value: 42");
    }

    [Fact]
    public void ValueExpression_TemplateWithNestedHeaderObject()
    {
        // ${header.MyObject.PropInt} inside a template
        var template = ExpressionResolver.GetCompiledTemplate("Header: ${header.info.Status}");
        var exchange = CreateExchange("body");
        exchange.In.Headers["info"] = new { Status = "OK" };
        template(exchange).Should().Be("Header: OK");
    }

    [Fact]
    public void ValueExpression_LogicalExprWithNestedProperty()
    {
        // Logical: property.obj.Count > 3
        var pred = ExpressionResolver.CompileLogicalPredicate("property.obj.Count > 3");
        var exchange = CreateExchange("body");
        exchange.Properties["obj"] = new { Count = 5 };
        pred(exchange).Should().BeTrue();
    }

    [Fact]
    public void ValueExpression_LogicalExprWithNestedPropertyFalse()
    {
        var pred = ExpressionResolver.CompileLogicalPredicate("property.obj.Count > 10");
        var exchange = CreateExchange("body");
        exchange.Properties["obj"] = new { Count = 5 };
        pred(exchange).Should().BeFalse();
    }

    #endregion

    #region Value expressions — arithmetic

    [Fact]
    public void ValueExpression_Subtraction()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("property.a - property.b");
        var exchange = CreateExchange("body");
        exchange.Properties["a"] = 20;
        exchange.Properties["b"] = 7;
        Convert.ToInt32(expr(exchange)).Should().Be(13);
    }

    [Fact]
    public void ValueExpression_Division()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("property.x / 2");
        var exchange = CreateExchange("body");
        exchange.Properties["x"] = 10;
        Convert.ToInt32(expr(exchange)).Should().Be(5);
    }

    [Fact]
    public void ValueExpression_OperatorPrecedence_MulBeforeAdd()
    {
        // a + b * c should be a + (b * c) = 2 + 3*4 = 14
        var expr = ExpressionResolver.GetCompiledValueExpression("property.a + property.b * property.c");
        var exchange = CreateExchange("body");
        exchange.Properties["a"] = 2;
        exchange.Properties["b"] = 3;
        exchange.Properties["c"] = 4;
        Convert.ToInt32(expr(exchange)).Should().Be(14);
    }

    [Fact]
    public void ValueExpression_Parentheses_OverridePrecedence()
    {
        // (a + b) * c = (2 + 3) * 4 = 20
        var expr = ExpressionResolver.GetCompiledValueExpression("(property.a + property.b) * property.c");
        var exchange = CreateExchange("body");
        exchange.Properties["a"] = 2;
        exchange.Properties["b"] = 3;
        exchange.Properties["c"] = 4;
        Convert.ToInt32(expr(exchange)).Should().Be(20);
    }

    [Fact]
    public void ValueExpression_StringConcatenation()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("'hello' + ' ' + 'world'");
        var exchange = CreateExchange("body");
        expr(exchange)?.ToString().Should().Be("hello world");
    }

    [Fact]
    public void ValueExpression_StringRepetition()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("'ab' * 3");
        var exchange = CreateExchange("body");
        expr(exchange)?.ToString().Should().Be("ababab");
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // 4. UNARY OPERATORS
    // ═══════════════════════════════════════════════════

    #region Unary operators

    [Fact]
    public void ValueExpression_UnaryNot()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("!property.flag");
        var exchange = CreateExchange("body");
        exchange.Properties["flag"] = true;
        expr(exchange).Should().Be(false);
    }

    [Fact]
    public void ValueExpression_UnaryMinus()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("-property.val");
        var exchange = CreateExchange("body");
        exchange.Properties["val"] = 42;
        Convert.ToDouble(expr(exchange)).Should().Be(-42);
    }

    [Fact]
    public void ValueExpression_UnaryPlus()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("+property.val");
        var exchange = CreateExchange("body");
        exchange.Properties["val"] = 42;
        Convert.ToDouble(expr(exchange)).Should().Be(42);
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // 5. INCREMENT / DECREMENT (AST pipeline)
    // ═══════════════════════════════════════════════════

    #region Increment / Decrement

    [Fact]
    public void ValueExpression_PrefixIncrement_ReturnsNewValue()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("++property.counter");
        var exchange = CreateExchange("body");
        exchange.Properties["counter"] = 5;
        var result = expr(exchange);
        Convert.ToInt32(result).Should().Be(6);
        Convert.ToInt32(exchange.Properties["counter"]).Should().Be(6);
    }

    [Fact]
    public void ValueExpression_PrefixDecrement_ReturnsNewValue()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("--property.counter");
        var exchange = CreateExchange("body");
        exchange.Properties["counter"] = 5;
        var result = expr(exchange);
        Convert.ToInt32(result).Should().Be(4);
        Convert.ToInt32(exchange.Properties["counter"]).Should().Be(4);
    }

    [Fact]
    public void ValueExpression_PostfixIncrement_ReturnsOldValue()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("property.counter++");
        var exchange = CreateExchange("body");
        exchange.Properties["counter"] = 5;
        var result = expr(exchange);
        Convert.ToInt32(result).Should().Be(5);  // old value
        Convert.ToInt32(exchange.Properties["counter"]).Should().Be(6);  // mutated
    }

    [Fact]
    public void ValueExpression_PostfixDecrement_ReturnsOldValue()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("property.counter--");
        var exchange = CreateExchange("body");
        exchange.Properties["counter"] = 5;
        var result = expr(exchange);
        Convert.ToInt32(result).Should().Be(5);  // old value
        Convert.ToInt32(exchange.Properties["counter"]).Should().Be(4);  // mutated
    }

    [Fact]
    public void ValueExpression_PrefixIncrement_NullInitializesToZero()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("++property.counter");
        var exchange = CreateExchange("body");
        // counter is not set → should auto-init to 0, then increment to 1
        var result = expr(exchange);
        Convert.ToInt32(result).Should().Be(1);
        Convert.ToInt32(exchange.Properties["counter"]).Should().Be(1);
    }

    [Fact]
    public void ValueExpression_PrefixIncrement_Header()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("++header.retryCount");
        var exchange = CreateExchange("body");
        exchange.In.Headers["retryCount"] = 2;
        var result = expr(exchange);
        Convert.ToInt32(result).Should().Be(3);
        Convert.ToInt32(exchange.In.Headers["retryCount"]).Should().Be(3);
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // 6. STRING METHODS (via ResolveExpression runtime)
    // ═══════════════════════════════════════════════════

    #region String methods

    [Fact]
    public void ResolveExpression_StringContains_True()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["text"] = "Hello World";
        var result = ExpressionResolver.ResolveExpression("property.text.contains('World')", exchange);
        result.Should().Be(true);
    }

    [Fact]
    public void ResolveExpression_StringContains_False()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["text"] = "Hello World";
        var result = ExpressionResolver.ResolveExpression("property.text.contains('Goodbye')", exchange);
        result.Should().Be(false);
    }

    [Fact]
    public void ResolveExpression_StringToUpper()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["text"] = "hello";
        var result = ExpressionResolver.ResolveExpression("property.text.toUpper()", exchange);
        result?.ToString().Should().Be("HELLO");
    }

    [Fact]
    public void ResolveExpression_StringToLower()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["text"] = "HELLO";
        var result = ExpressionResolver.ResolveExpression("property.text.toLower()", exchange);
        result?.ToString().Should().Be("hello");
    }

    [Fact]
    public void ResolveExpression_StringTrim()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["text"] = "  hello  ";
        var result = ExpressionResolver.ResolveExpression("property.text.trim()", exchange);
        result?.ToString().Should().Be("hello");
    }

    [Fact]
    public void ResolveExpression_StringReplace()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["text"] = "Hello World";
        var result = ExpressionResolver.ResolveExpression("property.text.replace('World', 'Universe')", exchange);
        result?.ToString().Should().Be("Hello Universe");
    }

    [Fact]
    public void ResolveExpression_StringStartsWith_True()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["text"] = "Hello World";
        var result = ExpressionResolver.ResolveExpression("property.text.startsWith('Hello')", exchange);
        result.Should().Be(true);
    }

    [Fact]
    public void ResolveExpression_StringEndsWith_True()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["text"] = "Hello World";
        var result = ExpressionResolver.ResolveExpression("property.text.endsWith('World')", exchange);
        result.Should().Be(true);
    }

    [Fact]
    public void ResolveExpression_StringIndexOf()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["text"] = "Hello World";
        var result = ExpressionResolver.ResolveExpression("property.text.indexOf('World')", exchange);
        Convert.ToInt32(result).Should().Be(6);
    }

    [Fact]
    public void ResolveExpression_StringLength()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["text"] = "Hello";
        var result = ExpressionResolver.ResolveExpression("property.text.length()", exchange);
        Convert.ToInt32(result).Should().Be(5);
    }

    [Fact]
    public void ResolveExpression_StringSubstring_OneArg()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["text"] = "Hello World";
        var result = ExpressionResolver.ResolveExpression("property.text.substring(6)", exchange);
        result?.ToString().Should().Be("World");
    }

    [Fact]
    public void ResolveExpression_StringSubstring_TwoArgs()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["text"] = "Hello World";
        var result = ExpressionResolver.ResolveExpression("property.text.substring(0, 5)", exchange);
        result?.ToString().Should().Be("Hello");
    }

    [Fact]
    public void ResolveExpression_CollectionContains_True()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["list"] = new List<string> { "a", "b", "c" };
        var result = ExpressionResolver.ResolveExpression("property.list.contains('b')", exchange);
        result.Should().Be(true);
    }

    [Fact]
    public void ResolveExpression_CollectionLength()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["list"] = new List<int> { 1, 2, 3, 4, 5 };
        var result = ExpressionResolver.ResolveExpression("property.list.length()", exchange);
        Convert.ToInt32(result).Should().Be(5);
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // 7. TYPE CONVERSION / COERCION
    // ═══════════════════════════════════════════════════

    #region Type conversion

    [Fact]
    public void ParseLiteral_Null()
    {
        ExpressionResolver.ParseLiteral("null").Should().BeNull();
    }

    [Fact]
    public void ParseLiteral_SingleQuotedString()
    {
        ExpressionResolver.ParseLiteral("'hello'").Should().Be("hello");
    }

    [Fact]
    public void ParseLiteral_DoubleQuotedString()
    {
        ExpressionResolver.ParseLiteral("\"world\"").Should().Be("world");
    }

    [Fact]
    public void ParseLiteral_Integer()
    {
        ExpressionResolver.ParseLiteral("42").Should().Be(42);
    }

    [Fact]
    public void ParseLiteral_Double()
    {
        var result = ExpressionResolver.ParseLiteral("3.14");
        Convert.ToDouble(result).Should().BeApproximately(3.14, 0.001);
    }

    [Fact]
    public void ParseLiteral_BoolTrue()
    {
        ExpressionResolver.ParseLiteral("true").Should().Be(true);
    }

    [Fact]
    public void ParseLiteral_BoolFalse()
    {
        ExpressionResolver.ParseLiteral("false").Should().Be(false);
    }

    [Fact]
    public void TryConvertToBool_NullIsFalse()
    {
        ExpressionResolver.TryConvertToBool(null, out var result).Should().BeTrue();
        result.Should().BeFalse();
    }

    [Fact]
    public void TryConvertToBool_BoolPassthrough()
    {
        ExpressionResolver.TryConvertToBool(true, out var result).Should().BeTrue();
        result.Should().BeTrue();
    }

    [Fact]
    public void TryConvertToBool_StringTrue()
    {
        ExpressionResolver.TryConvertToBool("true", out var result).Should().BeTrue();
        result.Should().BeTrue();
    }

    [Fact]
    public void TryConvertToBool_StringYes()
    {
        ExpressionResolver.TryConvertToBool("yes", out var result).Should().BeTrue();
        result.Should().BeTrue();
    }

    [Fact]
    public void TryConvertToBool_StringNo()
    {
        ExpressionResolver.TryConvertToBool("no", out var result).Should().BeTrue();
        result.Should().BeFalse();
    }

    [Fact]
    public void TryConvertToBool_Int_NonZeroIsTrue()
    {
        ExpressionResolver.TryConvertToBool(1, out var result).Should().BeTrue();
        result.Should().BeTrue();
    }

    [Fact]
    public void TryConvertToBool_Int_ZeroIsFalse()
    {
        ExpressionResolver.TryConvertToBool(0, out var result).Should().BeTrue();
        result.Should().BeFalse();
    }

    [Fact]
    public void TryConvertToBool_StringDa_Russian()
    {
        ExpressionResolver.TryConvertToBool("да", out var result).Should().BeTrue();
        result.Should().BeTrue();
    }

    [Fact]
    public void TryConvertToBool_StringNet_Russian()
    {
        ExpressionResolver.TryConvertToBool("нет", out var result).Should().BeTrue();
        result.Should().BeFalse();
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // 8. RUNTIME RESOLVERS — ResolveExpression
    // ═══════════════════════════════════════════════════

    #region ResolveExpression

    [Fact]
    public void ResolveExpression_PropertyAccess()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["name"] = "Alice";
        var result = ExpressionResolver.ResolveExpression("property.name", exchange);
        result?.ToString().Should().Be("Alice");
    }

    [Fact]
    public void ResolveExpression_HeaderAccess()
    {
        var exchange = CreateExchange("body");
        exchange.In.Headers["ContentType"] = "text/plain";
        var result = ExpressionResolver.ResolveExpression("header.ContentType", exchange);
        result?.ToString().Should().Be("text/plain");
    }

    [Fact]
    public void ResolveExpression_BodyAccess()
    {
        var exchange = CreateExchange("hello world");
        var result = ExpressionResolver.ResolveExpression("body", exchange);
        result?.ToString().Should().Be("hello world");
    }

    [Fact]
    public void ResolveExpression_PrefixIncrement()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["counter"] = 10;
        var result = ExpressionResolver.ResolveExpression("++property.counter", exchange);
        Convert.ToInt32(result).Should().Be(11);
        Convert.ToInt32(exchange.Properties["counter"]).Should().Be(11);
    }

    [Fact]
    public void ResolveExpression_PostfixIncrement()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["counter"] = 10;
        var result = ExpressionResolver.ResolveExpression("property.counter++", exchange);
        Convert.ToInt32(result).Should().Be(10);  // old value
        Convert.ToInt32(exchange.Properties["counter"]).Should().Be(11);  // mutated
    }

    [Fact]
    public void ResolveExpression_PrefixDecrement()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["counter"] = 10;
        var result = ExpressionResolver.ResolveExpression("--property.counter", exchange);
        Convert.ToInt32(result).Should().Be(9);
        Convert.ToInt32(exchange.Properties["counter"]).Should().Be(9);
    }

    [Fact]
    public void ResolveExpression_PostfixDecrement()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["counter"] = 10;
        var result = ExpressionResolver.ResolveExpression("property.counter--", exchange);
        Convert.ToInt32(result).Should().Be(10);  // old value
        Convert.ToInt32(exchange.Properties["counter"]).Should().Be(9);  // mutated
    }

    [Fact]
    public void ResolveExpression_LogicalExpression()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["x"] = 10;
        var result = ExpressionResolver.ResolveExpression("property.x > 5", exchange);
        result.Should().Be(true);
    }

    [Fact]
    public void ResolveExpression_UnaryMinus()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["val"] = 42;
        var result = ExpressionResolver.ResolveExpression("-property.val", exchange);
        Convert.ToDouble(result).Should().Be(-42);
    }

    [Fact]
    public void ResolveExpression_UnaryNot()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["flag"] = true;
        var result = ExpressionResolver.ResolveExpression("!property.flag", exchange);
        result.Should().Be(false);
    }

    [Fact]
    public void ResolveExpression_EmptyString_ReturnsNull()
    {
        var exchange = CreateExchange("body");
        ExpressionResolver.ResolveExpression("", exchange).Should().BeNull();
    }

    [Fact]
    public void ResolveExpression_NullString_ReturnsNull()
    {
        var exchange = CreateExchange("body");
        ExpressionResolver.ResolveExpression(null!, exchange).Should().BeNull();
    }

    [Fact]
    public void ResolveExpression_TemplateInsideResolve()
    {
        var exchange = CreateExchange("body");
        exchange.In.Headers["name"] = "Bob";
        var result = ExpressionResolver.ResolveExpression("${header.name}", exchange);
        result?.ToString().Should().Be("Bob");
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // 9. EDGE CASES & ERROR HANDLING
    // ═══════════════════════════════════════════════════

    #region Edge cases

    [Fact]
    public void Addition_Null_Plus_Value_ReturnsValue()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("property.a + property.b");
        var exchange = CreateExchange("body");
        // a is not set (null), b = 10
        exchange.Properties["b"] = 10;
        var result = expr(exchange);
        Convert.ToInt32(result).Should().Be(10);
    }

    [Fact]
    public void Multiplication_Null_Times_Value_ReturnsNull()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("property.a * property.b");
        var exchange = CreateExchange("body");
        // a is not set (null), b = 10
        exchange.Properties["b"] = 10;
        expr(exchange).Should().BeNull();
    }

    [Fact]
    public void Division_ByZero_ReturnsNull()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("property.a / property.b");
        var exchange = CreateExchange("body");
        exchange.Properties["a"] = 10;
        exchange.Properties["b"] = 0;
        expr(exchange).Should().BeNull();
    }

    [Fact]
    public void LogicalExpression_BothNulls_AreEqual()
    {
        var exchange = CreateExchange("body");
        // Neither property.a nor property.b is set → both null
        ExpressionResolver.EvaluateLogicalExpression("property.a == property.b", exchange)
            .Should().BeTrue();
    }

    [Fact]
    public void LogicalExpression_NullVsNonNull_NotEqual()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["b"] = "value";
        ExpressionResolver.EvaluateLogicalExpression("property.a != property.b", exchange)
            .Should().BeTrue();
    }

    [Fact]
    public void ValueExpression_MissingProperty_ReturnsNull()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("property.nonexistent");
        var exchange = CreateExchange("body");
        expr(exchange).Should().BeNull();
    }

    [Fact]
    public void ProcessTemplate_NullExchange_Throws()
    {
        var act = () => ExpressionResolver.ProcessTemplate("hello", null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void EvaluateLogicalExpression_EmptyString_Throws()
    {
        var exchange = CreateExchange("body");
        var act = () => ExpressionResolver.EvaluateLogicalExpression("", exchange);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Subtraction_Null_Minus_Value_ReturnsNull()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("property.a - property.b");
        var exchange = CreateExchange("body");
        exchange.Properties["b"] = 5;
        // a is null → subtraction returns left (null)
        expr(exchange).Should().BeNull();
    }

    [Fact]
    public void LogicalExpression_MixedType_StringVsInt()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["val"] = "10";
        // "10" == 10 should compare numerically
        ExpressionResolver.EvaluateLogicalExpression("property.val == 10", exchange)
            .Should().BeTrue();
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // 10. CACHING
    // ═══════════════════════════════════════════════════

    #region Caching

    [Fact]
    public void ClearTemplateCache_WorksIndependently()
    {
        ExpressionResolver.ClearAllCaches();
        ExpressionResolver.GetCompiledTemplate("test ${body}");
        ExpressionResolver.GetCompiledLogicalExpression("property.x == 1");
        ExpressionResolver.ClearTemplateCache();
        var stats = ExpressionResolver.GetCacheStatistics();
        stats.TemplateCount.Should().Be(0);
        stats.LogicalExpressionCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void ClearLogicalExpressionCache_WorksIndependently()
    {
        ExpressionResolver.ClearAllCaches();
        ExpressionResolver.GetCompiledTemplate("test ${body}");
        ExpressionResolver.GetCompiledLogicalExpression("property.x == 1");
        ExpressionResolver.ClearLogicalExpressionCache();
        var stats = ExpressionResolver.GetCacheStatistics();
        stats.TemplateCount.Should().BeGreaterThanOrEqualTo(1);
        stats.LogicalExpressionCount.Should().Be(0);
    }

    [Fact]
    public void ClearValueExpressionCache_WorksIndependently()
    {
        ExpressionResolver.ClearAllCaches();
        ExpressionResolver.GetCompiledValueExpression("property.x");
        ExpressionResolver.GetCompiledTemplate("test ${body}");
        ExpressionResolver.ClearValueExpressionCache();
        var stats = ExpressionResolver.GetCacheStatistics();
        stats.ValueExpressionCount.Should().Be(0);
        stats.TemplateCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void CacheStatistics_TrackHitsAndMisses()
    {
        ExpressionResolver.ClearAllCaches();
        var template = "cache-test ${body}";
        // First call = miss, second call = hit
        ExpressionResolver.GetCompiledTemplate(template);
        ExpressionResolver.GetCompiledTemplate(template);
        var stats = ExpressionResolver.GetCacheStatistics();
        stats.TemplateMisses.Should().BeGreaterThanOrEqualTo(1);
        stats.TemplateHits.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void ValueExpressionCache_SeparatesAstFromRegular()
    {
        ExpressionResolver.ClearAllCaches();
        // Regular expression goes into cache with key "property.x"
        ExpressionResolver.GetCompiledValueExpression("property.x");
        // AST expression goes into cache with key "ast:property.x"
        ExpressionResolver.GetCompiledValueExpressionWithAst("property.x");
        var stats = ExpressionResolver.GetCacheStatistics();
        stats.ValueExpressionCount.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void GetCompiledTemplate_WithContextId_IsolatesCache()
    {
        ExpressionResolver.ClearAllCaches();
        var template = "ctx-test ${body}";
        
        var fn1 = ExpressionResolver.GetCompiledTemplate(template, "ctx-A");
        var fn2 = ExpressionResolver.GetCompiledTemplate(template, "ctx-B");
        var fn3 = ExpressionResolver.GetCompiledTemplate(template); // global (no context)
        
        // All three should be in the cache as separate entries
        var stats = ExpressionResolver.GetCacheStatistics();
        stats.TemplateCount.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void ClearCachesForContext_RemovesOnlyScopedEntries()
    {
        ExpressionResolver.ClearAllCaches();
        
        // Populate caches with different contexts — capture references
        var t1 = ExpressionResolver.GetCompiledTemplate("clear-ctx-t1 ${body}", "ctx-1");
        var t2 = ExpressionResolver.GetCompiledTemplate("clear-ctx-t2 ${body}", "ctx-2");
        var t3 = ExpressionResolver.GetCompiledTemplate("clear-ctx-t3 ${body}"); // global
        
        var v1 = ExpressionResolver.GetCompiledValueExpression("property.x", "ctx-1");
        var v2 = ExpressionResolver.GetCompiledValueExpression("property.y", "ctx-2");
        
        var l1 = ExpressionResolver.GetCompiledLogicalExpression("property.a == 1", "ctx-1");
        
        // Clear only ctx-1
        ExpressionResolver.ClearCachesForContext("ctx-1");
        
        // ctx-1 entries should be evicted — re-fetch gives new instances
        ExpressionResolver.GetCompiledTemplate("clear-ctx-t1 ${body}", "ctx-1")
            .Should().NotBeSameAs(t1, "ctx-1 template should have been evicted");
        ExpressionResolver.GetCompiledValueExpression("property.x", "ctx-1")
            .Should().NotBeSameAs(v1, "ctx-1 value expression should have been evicted");
        ExpressionResolver.GetCompiledLogicalExpression("property.a == 1", "ctx-1")
            .Should().NotBeSameAs(l1, "ctx-1 logical expression should have been evicted");
        
        // ctx-2 and global entries should still be cached (same references)
        ExpressionResolver.GetCompiledTemplate("clear-ctx-t2 ${body}", "ctx-2")
            .Should().BeSameAs(t2, "ctx-2 template should still be cached");
        ExpressionResolver.GetCompiledTemplate("clear-ctx-t3 ${body}")
            .Should().BeSameAs(t3, "global template should still be cached");
        ExpressionResolver.GetCompiledValueExpression("property.y", "ctx-2")
            .Should().BeSameAs(v2, "ctx-2 value expression should still be cached");
    }

    [Fact]
    public void ProcessTemplate_UsesExchangeRouteId_ForCacheIsolation()
    {
        ExpressionResolver.ClearAllCaches();
        
        var exchange1 = CreateExchange("hello");
        exchange1.RouteId = "route-alpha";
        
        var exchange2 = CreateExchange("world");
        exchange2.RouteId = "route-beta";
        
        // Process same template with different route IDs
        ExpressionResolver.ProcessTemplate("${body}", exchange1);
        ExpressionResolver.ProcessTemplate("${body}", exchange2);
        
        // Should have 2 separate cache entries
        var stats = ExpressionResolver.GetCacheStatistics();
        stats.TemplateCount.Should().BeGreaterThanOrEqualTo(2);
        
        // Clear only route-alpha
        ExpressionResolver.ClearCachesForContext("route-alpha");
        stats = ExpressionResolver.GetCacheStatistics();
        stats.TemplateCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void ProcessTemplate_WithNullRouteId_UsesGlobalCache()
    {
        ExpressionResolver.ClearAllCaches();
        
        var exchange = CreateExchange("test");
        // RouteId is null by default
        exchange.RouteId.Should().BeNull();
        
        ExpressionResolver.ProcessTemplate("${body}", exchange);
        ExpressionResolver.ProcessTemplate("${body}", exchange); // should hit cache
        
        var stats = ExpressionResolver.GetCacheStatistics();
        stats.TemplateCount.Should().Be(1);
        stats.TemplateHits.Should().BeGreaterThanOrEqualTo(1);
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // 11. COMPILED LOGICAL PREDICATE
    // ═══════════════════════════════════════════════════

    #region Compiled predicates

    [Fact]
    public void CompileLogicalPredicate_ComplexExpression()
    {
        var pred = ExpressionResolver.CompileLogicalPredicate(
            "property.status == 'active' AND property.count > 0");
        
        var ex1 = CreateExchange("body");
        ex1.Properties["status"] = "active";
        ex1.Properties["count"] = 5;
        pred(ex1).Should().BeTrue();
        
        var ex2 = CreateExchange("body");
        ex2.Properties["status"] = "active";
        ex2.Properties["count"] = 0;
        pred(ex2).Should().BeFalse();
        
        var ex3 = CreateExchange("body");
        ex3.Properties["status"] = "inactive";
        ex3.Properties["count"] = 5;
        pred(ex3).Should().BeFalse();
    }

    [Fact]
    public void CompileLogicalPredicate_HeaderComparison()
    {
        var pred = ExpressionResolver.CompileLogicalPredicate("header.status == 200");
        
        var ex1 = CreateExchange("body");
        ex1.In.Headers["status"] = 200;
        pred(ex1).Should().BeTrue();
        
        var ex2 = CreateExchange("body");
        ex2.In.Headers["status"] = 404;
        pred(ex2).Should().BeFalse();
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // 12. COMPILED TEMPLATE — GetCompiledTemplate
    // ═══════════════════════════════════════════════════

    #region Compiled template delegates

    [Fact]
    public void GetCompiledTemplate_ReusableAcrossDifferentExchanges()
    {
        var template = ExpressionResolver.GetCompiledTemplate("Hello, ${header.name}!");
        
        var ex1 = CreateExchange("body");
        ex1.In.Headers["name"] = "Alice";
        template(ex1).Should().Be("Hello, Alice!");
        
        var ex2 = CreateExchange("body");
        ex2.In.Headers["name"] = "Bob";
        template(ex2).Should().Be("Hello, Bob!");
    }

    [Fact]
    public void GetCompiledTemplate_WithProperty()
    {
        var template = ExpressionResolver.GetCompiledTemplate("User: ${property.userId}");
        var exchange = CreateExchange("body");
        exchange.Properties["userId"] = "u-42";
        template(exchange).Should().Be("User: u-42");
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // 13. RESOLVE EXPRESSION — FULL CHAIN 
    // ═══════════════════════════════════════════════════

    #region ResolveExpression full chain

    [Fact]
    public void ResolveExpression_NOT_Expression()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["flag"] = true;
        var result = ExpressionResolver.ResolveExpression("NOT property.flag", exchange);
        result.Should().Be(false);
    }

    [Fact]
    public void ResolveExpression_AND_Expression()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["a"] = 10;
        exchange.Properties["b"] = 20;
        var result = ExpressionResolver.ResolveExpression("property.a > 5 AND property.b > 15", exchange);
        result.Should().Be(true);
    }

    [Fact]
    public void ResolveExpression_DefaultProperty_FallsThrough()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["custom"] = "value";
        // Expression without prefix → default property lookup
        var result = ExpressionResolver.ResolveExpression("custom", exchange);
        result?.ToString().Should().Be("value");
    }

    #endregion

    #region Dictionary body index access

    [Fact]
    public void ProcessTemplate_BodyDictIndexAccess_ResolvesValues()
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = 42,
            ["message"] = "hello world",
            ["status"] = "active"
        };
        var exchange = CreateExchange(dict);

        var result = ExpressionResolver.ProcessTemplate(
            "id=${body['id']}, msg=${body['message']}, status=${body['status']}", exchange);

        result.Should().Be("id=42, msg=hello world, status=active");
    }

    [Fact]
    public void ProcessTemplate_BodyDictIndexAccess_NullableObjectDict()
    {
        // Matches what SQL component returns: Dictionary<string, object?>
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = 1,
            ["name"] = "test",
            ["value"] = null
        };
        var exchange = CreateExchange(dict);

        ExpressionResolver.ProcessTemplate("${body['id']}", exchange).Should().Be("1");
        ExpressionResolver.ProcessTemplate("${body['name']}", exchange).Should().Be("test");
        ExpressionResolver.ProcessTemplate("${body['value']}", exchange).Should().BeEmpty();
    }

    #endregion
}
