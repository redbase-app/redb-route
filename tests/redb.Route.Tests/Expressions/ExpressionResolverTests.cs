using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// Tests for ExpressionResolver — compiled template processing,
/// logical expressions, value expressions, and caching.
/// </summary>
[Collection("ExpressionResolver")]
public class ExpressionResolverTests : IDisposable
{
    public ExpressionResolverTests()
    {
        ExpressionResolver.ClearAllCaches();
    }

    public void Dispose()
    {
        ExpressionResolver.ClearAllCaches();
    }

    private static IExchange CreateExchange(object? body = null)
        => new Exchange(new Message(body));

    // ── Template processing ──

    [Fact]
    public void ProcessTemplate_PlainText_ReturnsUnchanged()
    {
        var exchange = CreateExchange("body");
        var result = ExpressionResolver.ProcessTemplate("Hello World", exchange);
        result.Should().Be("Hello World");
    }

    [Fact]
    public void ProcessTemplate_BodyExpression()
    {
        var exchange = CreateExchange("MyBody");
        var result = ExpressionResolver.ProcessTemplate("Body is: ${body}", exchange);
        result.Should().Be("Body is: MyBody");
    }

    [Fact]
    public void ProcessTemplate_HeaderExpression()
    {
        var exchange = CreateExchange("body");
        exchange.In.Headers["correlationId"] = "abc-123";
        var result = ExpressionResolver.ProcessTemplate("ID: ${header.correlationId}", exchange);
        result.Should().Be("ID: abc-123");
    }

    [Fact]
    public void ProcessTemplate_PropertyExpression()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["userId"] = "user42";
        var result = ExpressionResolver.ProcessTemplate("User: ${property.userId}", exchange);
        result.Should().Be("User: user42");
    }

    [Fact]
    public void ProcessTemplate_ContentTypeExpression()
    {
        var exchange = CreateExchange("body");
        exchange.In.ContentType = "application/json";
        var result = ExpressionResolver.ProcessTemplate("CT: ${contentType}", exchange);
        result.Should().Be("CT: application/json");
    }

    [Fact]
    public void ProcessTemplate_ContentTypeExpression_NullReturnsEmpty()
    {
        var exchange = CreateExchange("body");
        exchange.In.ContentType = null;
        var result = ExpressionResolver.ProcessTemplate("CT: ${contentType}", exchange);
        result.Should().Be("CT: ");
    }

    [Fact]
    public void ProcessTemplate_MultipleExpressions()
    {
        var exchange = CreateExchange("payload");
        exchange.In.Headers["type"] = "request";
        exchange.Properties["id"] = 42;
        var result = ExpressionResolver.ProcessTemplate(
            "${header.type}: ${body} [${property.id}]", exchange);
        result.Should().Be("request: payload [42]");
    }

    // ── Logical expressions ──

    [Fact]
    public void EvaluateLogicalExpression_SimpleEquality_True()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["status"] = "active";
        var result = ExpressionResolver.EvaluateLogicalExpression(
            "property.status == 'active'", exchange);
        result.Should().BeTrue();
    }

    [Fact]
    public void EvaluateLogicalExpression_SimpleEquality_False()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["status"] = "inactive";
        var result = ExpressionResolver.EvaluateLogicalExpression(
            "property.status == 'active'", exchange);
        result.Should().BeFalse();
    }

    [Fact]
    public void EvaluateLogicalExpression_ContentTypeEquality()
    {
        var exchange = CreateExchange("body");
        exchange.In.ContentType = "application/json";
        var result = ExpressionResolver.EvaluateLogicalExpression(
            "contentType == 'application/json'", exchange);
        result.Should().BeTrue();
    }

    [Fact]
    public void EvaluateLogicalExpression_NumericComparison()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["count"] = 10;
        var result = ExpressionResolver.EvaluateLogicalExpression(
            "property.count > 5", exchange);
        result.Should().BeTrue();
    }

    [Fact]
    public void EvaluateLogicalExpression_AndOperator()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["a"] = 10;
        exchange.Properties["b"] = 20;
        var result = ExpressionResolver.EvaluateLogicalExpression(
            "property.a > 5 AND property.b > 15", exchange);
        result.Should().BeTrue();
    }

    [Fact]
    public void EvaluateLogicalExpression_OrOperator()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["a"] = 3;
        exchange.Properties["b"] = 20;
        var result = ExpressionResolver.EvaluateLogicalExpression(
            "property.a > 5 OR property.b > 15", exchange);
        result.Should().BeTrue();
    }

    [Fact]
    public void EvaluateLogicalExpression_NotOperator()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["flag"] = false;
        var result = ExpressionResolver.EvaluateLogicalExpression(
            "NOT property.flag", exchange);
        result.Should().BeTrue();
    }

    // ── Value expressions ──

    [Fact]
    public void GetCompiledValueExpression_PropertyAccess()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("property.name");
        var exchange = CreateExchange("body");
        exchange.Properties["name"] = "TestValue";
        expr(exchange).Should().Be("TestValue");
    }

    [Fact]
    public void GetCompiledValueExpression_BodyAccess()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("body");
        var exchange = CreateExchange("hello world");
        expr(exchange)?.ToString().Should().Be("hello world");
    }

    [Fact]
    public void GetCompiledValueExpression_HeaderAccess()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("header.ContentType");
        var exchange = CreateExchange("body");
        exchange.In.Headers["ContentType"] = "text/plain";
        expr(exchange).Should().Be("text/plain");
    }

    [Fact]
    public void GetCompiledValueExpression_Literal()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("42");
        var exchange = CreateExchange("body");
        Convert.ToInt32(expr(exchange)).Should().Be(42);
    }

    [Fact]
    public void GetCompiledValueExpression_StringLiteral()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("'hello'");
        var exchange = CreateExchange("body");
        expr(exchange).Should().Be("hello");
    }

    // ── Arithmetic expressions ──

    [Fact]
    public void GetCompiledValueExpression_Addition()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("property.a + property.b");
        var exchange = CreateExchange("body");
        exchange.Properties["a"] = 10;
        exchange.Properties["b"] = 20;
        Convert.ToInt32(expr(exchange)).Should().Be(30);
    }

    [Fact]
    public void GetCompiledValueExpression_Multiplication()
    {
        var expr = ExpressionResolver.GetCompiledValueExpression("property.x * 3");
        var exchange = CreateExchange("body");
        exchange.Properties["x"] = 7;
        Convert.ToInt32(expr(exchange)).Should().Be(21);
    }

    // ── Compiled template caching ──

    [Fact]
    public void GetCompiledTemplate_CachesResult()
    {
        var template = "Hello ${body}";
        var compiled1 = ExpressionResolver.GetCompiledTemplate(template);
        var compiled2 = ExpressionResolver.GetCompiledTemplate(template);
        compiled1.Should().BeSameAs(compiled2);
    }

    [Fact]
    public void GetCacheStatistics_ReportsCorrectCounts()
    {
        ExpressionResolver.ClearAllCaches();
        ExpressionResolver.GetCompiledTemplate("test ${body}");
        ExpressionResolver.GetCompiledLogicalExpression("property.x == 1");
        var stats = ExpressionResolver.GetCacheStatistics();
        stats.TemplateCount.Should().BeGreaterThanOrEqualTo(1);
        stats.LogicalExpressionCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void ClearAllCaches_EmptiesAll()
    {
        ExpressionResolver.GetCompiledTemplate("test ${body}");
        ExpressionResolver.ClearAllCaches();
        var stats = ExpressionResolver.GetCacheStatistics();
        stats.TemplateCount.Should().Be(0);
        stats.LogicalExpressionCount.Should().Be(0);
    }

    // ── CompileLogicalPredicate ──

    [Fact]
    public void CompileLogicalPredicate_ReturnsReusableDelegate()
    {
        var pred = ExpressionResolver.CompileLogicalPredicate("property.val > 5");
        var ex1 = CreateExchange("body");
        ex1.Properties["val"] = 10;
        pred(ex1).Should().BeTrue();

        var ex2 = CreateExchange("body");
        ex2.Properties["val"] = 3;
        pred(ex2).Should().BeFalse();
    }

    // ── Exception expression ──

    [Fact]
    public void ProcessTemplate_ExceptionMessage()
    {
        var exchange = CreateExchange("body");
        exchange.Exception = new InvalidOperationException("Something went wrong");
        var result = ExpressionResolver.ProcessTemplate("Error: ${exception.Message}", exchange);
        result.Should().Be("Error: Something went wrong");
    }

    [Fact]
    public void ProcessTemplate_ExceptionNull_ReturnsEmpty()
    {
        var exchange = CreateExchange("body");
        var result = ExpressionResolver.ProcessTemplate("Error: ${exception.Message}", exchange);
        result.Should().Be("Error: ");
    }
}
