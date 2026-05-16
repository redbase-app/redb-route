using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using FluentAssertions;

namespace redb.Route.Tests.Core;

public class RouteBuilderHelpersTests
{
    /// <summary>Exposes protected helpers for testing.</summary>
    private sealed class TestableBuilder : RouteBuilder
    {
        protected override void Configure() { }

        // Expose helpers
        public static BodyExpression CallBody() => Body();
        public static HeaderExpression CallHeader(string name) => Header(name);
        public static PropertyExpression CallProperty(string name) => Property(name);
        public static ConstantExpression CallConstant(object value) => Constant(value);
        public static ExchangeExpression CallExchange(Func<IExchange, object?> func) => Exchange(func);
        public static JsonPathExpression CallJPath(string path) => JPath(path);
        public static redb.Route.Expressions.XPathExpression CallXPath(string path) => XPath(path);
    }

    // ── Body() ──

    [Fact]
    public void Body_ReturnsBodyExpression()
    {
        var expr = TestableBuilder.CallBody();
        expr.Should().BeOfType<BodyExpression>();
    }

    [Fact]
    public void Body_EvaluatesExchangeBody()
    {
        var expr = TestableBuilder.CallBody();
        var exchange = new Exchange(new Message("test-body"));
        expr.Evaluate<string>(exchange).Should().Be("test-body");
    }

    // ── Header(name) ──

    [Fact]
    public void Header_ReturnsHeaderExpression()
    {
        var expr = TestableBuilder.CallHeader("X-Custom");
        expr.Should().BeOfType<HeaderExpression>();
    }

    [Fact]
    public void Header_EvaluatesHeaderValue()
    {
        var expr = TestableBuilder.CallHeader("status");
        var exchange = new Exchange(new Message("body"));
        exchange.In.Headers["status"] = "active";
        expr.Evaluate<string>(exchange).Should().Be("active");
    }

    // ── Property(name) ──

    [Fact]
    public void Property_ReturnsPropertyExpression()
    {
        var expr = TestableBuilder.CallProperty("retry");
        expr.Should().BeOfType<PropertyExpression>();
    }

    [Fact]
    public void Property_EvaluatesPropertyValue()
    {
        var expr = TestableBuilder.CallProperty("count");
        var exchange = new Exchange(new Message("body"));
        exchange.Properties["count"] = 42;
        expr.Evaluate<int>(exchange).Should().Be(42);
    }

    // ── Constant(value) ──

    [Fact]
    public void Constant_ReturnsConstantExpression()
    {
        var expr = TestableBuilder.CallConstant(99);
        expr.Should().BeOfType<ConstantExpression>();
    }

    [Fact]
    public void Constant_AlwaysReturnsSameValue()
    {
        var expr = TestableBuilder.CallConstant("fixed");
        var exchange = new Exchange(new Message("anything"));
        expr.Evaluate<string>(exchange).Should().Be("fixed");
    }

    // ── Exchange(func) ──

    [Fact]
    public void Exchange_ReturnsExchangeExpression()
    {
        var expr = TestableBuilder.CallExchange(e => e.In.Body);
        expr.Should().BeOfType<ExchangeExpression>();
    }

    [Fact]
    public void Exchange_EvaluatesDelegateAgainstExchange()
    {
        var expr = TestableBuilder.CallExchange(e =>
            $"{e.In.Headers["a"]}-{e.In.Headers["b"]}");

        var exchange = new Exchange(new Message("body"));
        exchange.In.Headers["a"] = "hello";
        exchange.In.Headers["b"] = "world";

        expr.Evaluate<string>(exchange).Should().Be("hello-world");
    }

    // ── Predicate chaining from helpers ──

    [Fact]
    public void Body_IsEqualTo_CreatesPredicate()
    {
        var predicate = TestableBuilder.CallBody().isEqualTo("expected");
        predicate.Should().NotBeNull();

        var exchange = new Exchange(new Message("expected"));
        predicate.Matches(exchange).Should().BeTrue();
    }

    [Fact]
    public void Header_IsGreaterThan_CreatesPredicate()
    {
        var predicate = TestableBuilder.CallHeader("age").isGreaterThan(18);
        predicate.Should().NotBeNull();

        var exchange = new Exchange(new Message("body"));
        exchange.In.Headers["age"] = 25;
        predicate.Matches(exchange).Should().BeTrue();
    }

    [Fact]
    public void Property_IsNotNull_CreatesPredicate()
    {
        var predicate = TestableBuilder.CallProperty("data").isNotNull();
        predicate.Should().NotBeNull();

        var exchange = new Exchange(new Message("body"));
        exchange.Properties["data"] = "something";
        predicate.Matches(exchange).Should().BeTrue();
    }

    // ── Fluent DSL integration: helpers inside route definition ──

    [Fact]
    public async Task Helpers_WorkInRouteDefinition()
    {
        object? captured = null;

        var context = new RouteContext();
        context.AddRoutes(r =>
        {
            r.From("direct://helpers-test")
                .SetBody(TestableBuilder.CallConstant("hello"))
                .SetHeader("src", TestableBuilder.CallBody())
                .Filter(TestableBuilder.CallHeader("src").isEqualTo("hello"))
                .Process(e => captured = e.In.Body);
        });

        await context.Start();
        try
        {
            var producer = context.GetEndpoint("direct://helpers-test").CreateProducer();
            await producer.Start();
            await producer.Process(new Exchange(new Message("ignored")));
            captured.Should().Be("hello");
        }
        finally
        {
            await context.Stop();
        }
    }
}
