using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// Tests for the Expression hierarchy (BodyExpression, HeaderExpression,
/// PropertyExpression, ConstantExpression, DelegateExpression, ExchangeExpression,
/// LogicalExpression).
/// </summary>
public class ExpressionTests
{
    private static IExchange CreateExchange(object? body = null)
    {
        var exchange = new Exchange(new Message(body));
        return exchange;
    }

    // ── BodyExpression ──

    [Fact]
    public void BodyExpression_ReturnsBody()
    {
        var expr = new BodyExpression();
        var exchange = CreateExchange("hello");
        expr.Evaluate<string>(exchange).Should().Be("hello");
    }

    [Fact]
    public void BodyExpression_ReturnsTypedBody()
    {
        var expr = new BodyExpression();
        var exchange = CreateExchange(42);
        expr.Evaluate<int>(exchange).Should().Be(42);
    }

    [Fact]
    public void BodyExpression_NullBody_ReturnsDefault()
    {
        var expr = new BodyExpression();
        var exchange = CreateExchange(null);
        expr.Evaluate<string>(exchange).Should().BeNull();
    }

    // ── HeaderExpression ──

    [Fact]
    public void HeaderExpression_ReturnsHeaderValue()
    {
        var expr = new HeaderExpression("ContentType");
        var exchange = CreateExchange("body");
        exchange.In.Headers["ContentType"] = "application/json";
        expr.Evaluate<string>(exchange).Should().Be("application/json");
    }

    [Fact]
    public void HeaderExpression_MissingHeader_ReturnsDefault()
    {
        var expr = new HeaderExpression("Missing");
        var exchange = CreateExchange("body");
        expr.Evaluate<string>(exchange).Should().BeNull();
    }

    // ── PropertyExpression ──

    [Fact]
    public void PropertyExpression_ReturnsPropertyValue()
    {
        var expr = new PropertyExpression("userId");
        var exchange = CreateExchange("body");
        exchange.Properties["userId"] = 123;
        expr.Evaluate<int>(exchange).Should().Be(123);
    }

    [Fact]
    public void PropertyExpression_MissingProperty_ReturnsDefault()
    {
        var expr = new PropertyExpression("missing");
        var exchange = CreateExchange("body");
        expr.Evaluate<string>(exchange).Should().BeNull();
    }

    // ── ConstantExpression ──

    [Fact]
    public void ConstantExpression_ReturnsConstantValue()
    {
        var expr = new ConstantExpression("fixed");
        var exchange = CreateExchange("body");
        expr.Evaluate<string>(exchange).Should().Be("fixed");
    }

    [Fact]
    public void ConstantExpression_ReturnsTypedConstant()
    {
        var expr = new ConstantExpression(42);
        var exchange = CreateExchange("body");
        expr.Evaluate<int>(exchange).Should().Be(42);
    }

    // ── DelegateExpression ──

    [Fact]
    public void DelegateExpression_ExecutesDelegate()
    {
        var expr = new DelegateExpression<string?>(ex => ex.In.Body?.ToString()?.ToUpper());
        var exchange = CreateExchange("hello");
        expr.Evaluate<string>(exchange).Should().Be("HELLO");
    }

    [Fact]
    public void DelegateExpression_WithTypedDelegate()
    {
        var expr = new DelegateExpression<int>(ex => (int)ex.In.Body! * 2);
        var exchange = CreateExchange(21);
        expr.Evaluate<int>(exchange).Should().Be(42);
    }

    // ── ExchangeExpression ──

    [Fact]
    public void ExchangeExpression_ReturnsEvaluatedValue()
    {
        var expr = new ExchangeExpression(ex => ex.In.Body);
        var exchange = CreateExchange("hello");
        expr.Evaluate<string>(exchange).Should().Be("hello");
    }

    // ── LogicalExpression ──

    [Fact]
    public void LogicalExpression_ReturnsPredicateResult_True()
    {
        var expr = new LogicalExpression("property.count > 3");
        var exchange = CreateExchange("body");
        exchange.Properties["count"] = 10;
        expr.Evaluate<bool>(exchange).Should().BeTrue();
    }

    [Fact]
    public void LogicalExpression_ReturnsPredicateResult_False()
    {
        var expr = new LogicalExpression("property.count > 100");
        var exchange = CreateExchange("body");
        exchange.Properties["count"] = 5;
        expr.Evaluate<bool>(exchange).Should().BeFalse();
    }
}
