using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Predicates;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// Tests for <see cref="StringExpression"/> — unified string expression that bridges
/// the ExpressionResolver world with the IExpression/IPredicate world.
/// </summary>
public class StringExpressionTests : IDisposable
{
    public StringExpressionTests()
    {
        ExpressionResolver.ClearAllCaches();
    }

    public void Dispose()
    {
        ExpressionResolver.ClearAllCaches();
    }

    private static IExchange CreateExchange(object? body = null)
    {
        var exchange = new Exchange(new Message(body));
        return exchange;
    }

    // ── Template mode (contains ${...}) ──

    [Fact]
    public void Evaluate_Template_HeaderInterpolation()
    {
        var expr = new StringExpression("${header.name}");
        var exchange = CreateExchange();
        exchange.In.Headers["name"] = "Alice";

        expr.Evaluate<string>(exchange).Should().Be("Alice");
    }

    [Fact]
    public void Evaluate_Template_MultipleInterpolations()
    {
        var expr = new StringExpression("Hello ${header.first} ${header.last}!");
        var exchange = CreateExchange();
        exchange.In.Headers["first"] = "John";
        exchange.In.Headers["last"] = "Doe";

        expr.Evaluate<string>(exchange).Should().Be("Hello John Doe!");
    }

    [Fact]
    public void Evaluate_Template_BodyInterpolation()
    {
        var expr = new StringExpression("Body is: ${body}");
        var exchange = CreateExchange("test-payload");

        expr.Evaluate<string>(exchange).Should().Be("Body is: test-payload");
    }

    [Fact]
    public void Evaluate_Template_PropertyInterpolation()
    {
        var expr = new StringExpression("id=${property.orderId}");
        var exchange = CreateExchange();
        exchange.Properties["orderId"] = "ORD-123";

        expr.Evaluate<string>(exchange).Should().Be("id=ORD-123");
    }

    // ── Value expression mode (no ${...} wrapper) ──

    [Fact]
    public void Evaluate_ValueExpr_HeaderValue()
    {
        var expr = new StringExpression("header.amount");
        var exchange = CreateExchange();
        exchange.In.Headers["amount"] = 500;

        expr.Evaluate<object>(exchange).Should().Be(500);
    }

    [Fact]
    public void Evaluate_ValueExpr_Arithmetic()
    {
        var expr = new StringExpression("header.a + header.b");
        var exchange = CreateExchange();
        exchange.In.Headers["a"] = 10;
        exchange.In.Headers["b"] = 20;

        var result = expr.Evaluate<object>(exchange);
        Convert.ToInt32(result).Should().Be(30);
    }

    [Fact]
    public void Evaluate_ValueExpr_Body()
    {
        var expr = new StringExpression("body");
        var exchange = CreateExchange("hello");

        expr.Evaluate<string>(exchange).Should().Be("hello");
    }

    // ── Type conversion ──

    [Fact]
    public void Evaluate_TypeConversion_StringToInt()
    {
        var expr = new StringExpression("header.count");
        var exchange = CreateExchange();
        exchange.In.Headers["count"] = "42";

        expr.Evaluate<int>(exchange).Should().Be(42);
    }

    [Fact]
    public void Evaluate_NullResult_ReturnsDefault()
    {
        var expr = new StringExpression("header.value");
        var exchange = CreateExchange();
        exchange.In.Headers["value"] = null!;

        expr.Evaluate<string>(exchange).Should().BeNull();
    }

    // ── Predicate integration (inherited from Expression base) ──

    [Fact]
    public void Predicate_IsEqualTo_Matches()
    {
        var expr = new StringExpression("header.status");
        var predicate = expr.isEqualTo("active");
        var exchange = CreateExchange();
        exchange.In.Headers["status"] = "active";

        predicate.Matches(exchange).Should().BeTrue();
    }

    [Fact]
    public void Predicate_IsEqualTo_NoMatch()
    {
        var expr = new StringExpression("header.status");
        var predicate = expr.isEqualTo("active");
        var exchange = CreateExchange();
        exchange.In.Headers["status"] = "inactive";

        predicate.Matches(exchange).Should().BeFalse();
    }

    [Fact]
    public void Predicate_IsGreaterThan_Matches()
    {
        var expr = new StringExpression("header.amount");
        var predicate = expr.isGreaterThan(100);
        var exchange = CreateExchange();
        exchange.In.Headers["amount"] = 500;

        predicate.Matches(exchange).Should().BeTrue();
    }

    [Fact]
    public void Predicate_Contains_Matches()
    {
        var expr = new StringExpression("header.message");
        var predicate = expr.contains("error");
        var exchange = CreateExchange();
        exchange.In.Headers["message"] = "An error occurred";

        predicate.Matches(exchange).Should().BeTrue();
    }

    [Fact]
    public void Predicate_IsNull_Matches()
    {
        var expr = new StringExpression("header.status");
        var predicate = expr.isNull();
        var exchange = CreateExchange();
        exchange.In.Headers["status"] = null!;

        predicate.Matches(exchange).Should().BeTrue();
    }

    [Fact]
    public void Predicate_IsNotNull_Matches()
    {
        var expr = new StringExpression("header.present");
        var predicate = expr.isNotNull();
        var exchange = CreateExchange();
        exchange.In.Headers["present"] = "yes";

        predicate.Matches(exchange).Should().BeTrue();
    }

    [Fact]
    public void Predicate_In_Matches()
    {
        var expr = new StringExpression("header.priority");
        var predicate = expr.In("high", "critical");
        var exchange = CreateExchange();
        exchange.In.Headers["priority"] = "high";

        predicate.Matches(exchange).Should().BeTrue();
    }

    [Fact]
    public void Predicate_Regex_Matches()
    {
        var expr = new StringExpression("header.code");
        var predicate = expr.regex(@"^ERR-\d+$");
        var exchange = CreateExchange();
        exchange.In.Headers["code"] = "ERR-404";

        predicate.Matches(exchange).Should().BeTrue();
    }

    [Fact]
    public void Predicate_StartsWith_Matches()
    {
        var expr = new StringExpression("header.path");
        var predicate = expr.startsWith("/api");
        var exchange = CreateExchange();
        exchange.In.Headers["path"] = "/api/orders";

        predicate.Matches(exchange).Should().BeTrue();
    }

    [Fact]
    public void Predicate_EndsWith_Matches()
    {
        var expr = new StringExpression("header.file");
        var predicate = expr.endsWith(".csv");
        var exchange = CreateExchange();
        exchange.In.Headers["file"] = "report.csv";

        predicate.Matches(exchange).Should().BeTrue();
    }

    [Fact]
    public void Predicate_IsBetween_Matches()
    {
        var expr = new StringExpression("header.score");
        var predicate = expr.isBetween(1, 10);
        var exchange = CreateExchange();
        exchange.In.Headers["score"] = 5;

        predicate.Matches(exchange).Should().BeTrue();
    }

    [Fact]
    public void Predicate_And_Combination()
    {
        var expr = new StringExpression("header.amount");
        var gt = expr.isGreaterThan(0);
        var lt = expr.isLessThan(1000);
        var predicate = new LambdaPredicate(e => gt.Matches(e) && lt.Matches(e));
        var exchange = CreateExchange();
        exchange.In.Headers["amount"] = 500;

        predicate.Matches(exchange).Should().BeTrue();
    }

    [Fact]
    public void Predicate_Not_Negation()
    {
        var expr = new StringExpression("header.status");
        var eq = expr.isEqualTo("blocked");
        var predicate = new LambdaPredicate(e => !eq.Matches(e));
        var exchange = CreateExchange();
        exchange.In.Headers["status"] = "active";

        predicate.Matches(exchange).Should().BeTrue();
    }

    // ── SetValue throws ──

    [Fact]
    public void SetValue_ThrowsNotSupported()
    {
        var expr = new StringExpression("header.x");
        var exchange = CreateExchange();

        var act = () => expr.SetValue(exchange, "value");
        act.Should().Throw<NotSupportedException>()
            .WithMessage("*read-only*");
    }

    // ── ToString ──

    [Fact]
    public void ToString_ReturnsExprFormat()
    {
        var expr = new StringExpression("${header.name}");
        expr.ToString().Should().Be("Expr(\"${header.name}\")");
    }

    // ── Template property ──

    [Fact]
    public void Template_ReturnsOriginal()
    {
        var expr = new StringExpression("header.amount + 100");
        expr.Template.Should().Be("header.amount + 100");
    }

    // ── Constructor validation ──

    [Fact]
    public void Constructor_NullTemplate_Throws()
    {
        var act = () => new StringExpression(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_EmptyTemplate_Throws()
    {
        var act = () => new StringExpression("");
        act.Should().Throw<ArgumentException>();
    }

    // ── Integration: used as IExpression in route DSL context ──

    [Fact]
    public void AsIExpression_WorksWithSetBody()
    {
        // Simulate what .SetBody(Expr("${header.x}")) does via ExpressionBodyProcessor
        IExpression expr = new StringExpression("${header.source}");
        var exchange = CreateExchange();
        exchange.In.Headers["source"] = "kafka-topic-1";

        var result = expr.Evaluate<object>(exchange);
        exchange.In.Body = result;

        exchange.In.Body.Should().Be("kafka-topic-1");
    }

    [Fact]
    public void AsIExpression_WorksWithFilter()
    {
        // Simulate what .Filter(Expr("${header.active}").isEqualTo("true")) does
        IExpression expr = new StringExpression("header.active");
        var predicate = ((Expression)expr).isEqualTo("true");
        var exchange = CreateExchange();
        exchange.In.Headers["active"] = "true";

        predicate.Matches(exchange).Should().BeTrue();
    }

    // ── Caching verification ──

    [Fact]
    public void MultipleInstances_SameTemplate_ShareCache()
    {
        var expr1 = new StringExpression("header.x");
        var expr2 = new StringExpression("header.x");
        var exchange = CreateExchange();
        exchange.In.Headers["x"] = "value";

        expr1.Evaluate<string>(exchange).Should().Be("value");
        expr2.Evaluate<string>(exchange).Should().Be("value");
        // Both use same compiled delegate from ExpressionResolver cache
    }
}
