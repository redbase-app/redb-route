using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Predicates;

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

    // The hand-written logical branch was removed on 2026-08-28 - one language, one parser.
    // The semantic tests below migrated to the surviving condition path via these helpers.
    private static bool EvaluateCondition(string condition, IExchange exchange)
        => PredicateFactory.FromString(condition).Matches(exchange);

    private static Func<IExchange, bool> CompileCondition(string condition)
        => PredicateFactory.FromString(condition).Matches;

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

    // ── Condition strings (migrated from the removed logical branch) ──

    [Fact]
    public void Condition_SimpleEquality_True()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["status"] = "active";
        var result = EvaluateCondition(
            "property.status == 'active'", exchange);
        result.Should().BeTrue();
    }

    [Fact]
    public void Condition_SimpleEquality_False()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["status"] = "inactive";
        var result = EvaluateCondition(
            "property.status == 'active'", exchange);
        result.Should().BeFalse();
    }

    [Fact]
    public void Condition_ContentTypeEquality()
    {
        var exchange = CreateExchange("body");
        exchange.In.ContentType = "application/json";
        var result = EvaluateCondition(
            "contentType == 'application/json'", exchange);
        result.Should().BeTrue();
    }

    [Fact]
    public void Condition_NumericComparison()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["count"] = 10;
        var result = EvaluateCondition(
            "property.count > 5", exchange);
        result.Should().BeTrue();
    }

    [Fact]
    public void Condition_AndOperator()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["a"] = 10;
        exchange.Properties["b"] = 20;
        var result = EvaluateCondition(
            "property.a > 5 AND property.b > 15", exchange);
        result.Should().BeTrue();
    }

    [Fact]
    public void Condition_OrOperator()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["a"] = 3;
        exchange.Properties["b"] = 20;
        var result = EvaluateCondition(
            "property.a > 5 OR property.b > 15", exchange);
        result.Should().BeTrue();
    }

    [Fact]
    public void Condition_NotOperator()
    {
        var exchange = CreateExchange("body");
        exchange.Properties["flag"] = false;
        var result = EvaluateCondition(
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
        ExpressionResolver.GetCompiledValueExpression("property.x");
        var stats = ExpressionResolver.GetCacheStatistics();
        stats.TemplateCount.Should().BeGreaterThanOrEqualTo(1);
        stats.ValueExpressionCount.Should().BeGreaterThanOrEqualTo(1);
    }

    /// <summary>
    /// Proved per entry, by delegate identity, not by the size of the cache: the caches are process
    /// state, and a route test in another collection compiles a template between the clear and any
    /// count we could read, so "the cache is empty" is not observable here. The text is unique to this
    /// test, so nobody else can put it back — whatever else the process is doing, a hit on it after a
    /// clear is impossible.
    /// </summary>
    [Fact]
    public void ClearAllCaches_DropsWhatWasCached()
    {
        var unique = Guid.NewGuid().ToString("N");
        var template = $"clear-probe-{unique} ${{body}}";
        var value = $"property.clear_probe_{unique} + 1";

        var compiledTemplate = ExpressionResolver.GetCompiledTemplate(template);
        var compiledValue = ExpressionResolver.GetCompiledValueExpression(value);
        ExpressionResolver.GetCompiledTemplate(template).Should().BeSameAs(compiledTemplate, "the template entry is cached before the clear");
        ExpressionResolver.GetCompiledValueExpression(value).Should().BeSameAs(compiledValue, "the value entry is cached before the clear");

        ExpressionResolver.ClearAllCaches();

        // Both caches, not just the template one: "all" is the claim under test.
        ExpressionResolver.GetCompiledTemplate(template).Should().NotBeSameAs(compiledTemplate, "the clear dropped the template entry, so it is compiled afresh");
        ExpressionResolver.GetCompiledValueExpression(value).Should().NotBeSameAs(compiledValue, "the clear dropped the value entry too");
    }

    // ── CompileCondition ──

    [Fact]
    public void CompileCondition_ReturnsReusableDelegate()
    {
        var pred = CompileCondition("property.val > 5");
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
