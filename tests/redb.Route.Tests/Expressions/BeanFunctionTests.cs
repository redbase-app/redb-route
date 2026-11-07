using AwesomeAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Predicates;

namespace redb.Route.Tests.Expressions;

/// <summary>A bean the expression function calls.</summary>
public class PricingRules
{
    public int Calls;

    public double Gross(int qty, double price) { Calls++; return qty * price; }

    public bool IsGold(string? name) => name == "ann";

    public string Stamp(IExchange exchange, string mark) => $"{mark}:{exchange.In.Body}";

    public string Name { get; set; } = "rules";

    public Task<bool> CheckAsync(string name) => Task.FromResult(name == "ann");

    public ValueTask<int> CountAsync() => ValueTask.FromResult(1);
}

/// <summary>
/// The <c>bean:</c> function of the expression language (stage 2 step d): the bean from the exchange's context
/// registry, the method bound by the <c>bean:</c> URI rules, synchronous only, refused under the sandbox, resolved once
/// per context. Used the same way in templates, bare conditions, C# routes and XML routes.
/// </summary>
public sealed class BeanFunctionTests : IAsyncLifetime
{
    private readonly RouteContext _context = new();
    private readonly PricingRules _rules = new();

    public Task InitializeAsync()
    {
        _context.AddToRegistry("rules", _rules);
        _context.AddToRegistry("pricing-rules", _rules);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _context.DisposeAsync();

    private Exchange Message(object? body = null, params (string Key, object? Value)[] headers)
    {
        var exchange = new Exchange(new Message(body ?? "payload")) { Context = _context };
        foreach (var (key, value) in headers)
            exchange.In.Headers[key] = value;
        return exchange;
    }

    [Theory]
    [InlineData("${bean:rules.Gross(header.qty, header.price)}")]
    [InlineData("${bean:rules.Gross(${header.qty}, ${header.price})}")]
    [InlineData("${bean:pricing-rules.Gross(header.qty, 2.5)}")]
    public void ATemplateHole_CallsTheBean_AndKeepsTheType(string template)
    {
        new StringExpression(template).Evaluate<object>(Message(null, ("qty", 3), ("price", 2.5))).Should().Be(7.5);
    }

    [Fact]
    public void InText_TheResultIsRendered()
    {
        ExpressionResolver.ProcessTemplate("total ${bean:rules.Gross(header.qty, 2)} EUR", Message(null, ("qty", 4)))
            .Should().Be("total 8 EUR");
    }

    [Fact]
    public void AmbientExchange_AndAStringLiteral_Bind()
    {
        new StringExpression("${bean:rules.Stamp('x')}").Evaluate<string>(Message("body")).Should().Be("x:body");
    }

    [Fact]
    public void AMethodWithoutParentheses_IsCalledWithoutArguments()
    {
        new StringExpression("${bean:rules.CountAsync}").Invoking(e => e.Evaluate<object>(Message()))
            .Should().Throw<InvalidOperationException>().WithMessage("*synchronous*");
    }

    [Theory]
    [InlineData("bean:rules.IsGold(header.name)", "ann", true)]
    [InlineData("bean:rules.IsGold(header.name)", "bob", false)]
    [InlineData("header.vip == true && bean:rules.IsGold(header.name)", "ann", true)]
    [InlineData("${bean:rules.IsGold(header.name)}", "ann", true)]
    public void AsACondition_BareAndTemplated(string condition, string name, bool expected)
    {
        PredicateFactory.FromString(condition).Matches(Message(null, ("name", name), ("vip", true))).Should().Be(expected);
    }

    [Fact]
    public void AMissingBean_NamesTheReference()
    {
        var act = () => new StringExpression("${bean:absent.Gross(1, 2)}").Evaluate<object>(Message());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("bean:absent.Gross: 'absent' is not in the context registry. Register it with AddToRegistry or a <bean> declaration.");
    }

    [Theory]
    [InlineData("${bean:rules.Missing(1)}", "*'#rules' has no public method 'Missing' taking 1 argument(s)*")]
    [InlineData("${bean:rules.Gross(1)}", "*'#rules' has no public method 'Gross' taking 1 argument(s)*")]
    public void AWrongMethodOrArity_IsTheBindingError(string template, string message)
    {
        var act = () => new StringExpression(template).Evaluate<object>(Message());

        act.Should().Throw<InvalidOperationException>().WithMessage(message);
    }

    [Theory]
    [InlineData("${bean:rules.CheckAsync(header.name)}", "Task`1")]
    [InlineData("${bean:rules.CountAsync()}", "ValueTask`1")]
    public void AnAsyncMethod_IsRefused_PointingToTheStep(string template, string returnType)
    {
        var act = () => new StringExpression(template).Evaluate<object>(Message(null, ("name", "ann")));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*returns {returnType}: a bean: call in an expression is synchronous. Call an asynchronous method as a step: <bean ref=\"#rules\"*");
    }

    [Fact]
    public void UnderTheSandbox_TheCallIsRefused()
    {
        var expression = new StringExpression("${bean:rules.Gross(1, 2)}");
        using var sandbox = ExpressionSandbox.Enter();

        var act = () => expression.Evaluate<object>(Message());

        act.Should().Throw<ExpressionSandboxViolationException>();
        _rules.Calls.Should().Be(0, "nothing ran");
    }

    [Fact]
    public void WithoutAContext_TheErrorSaysWhy()
    {
        var act = () => new StringExpression("${bean:rules.Gross(1, 2)}").Evaluate<object>(new Exchange());

        act.Should().Throw<InvalidOperationException>().WithMessage("*has no route context*");
    }

    [Fact]
    public async Task EachContext_ResolvesItsOwnBean_ThroughOneCompiledExpression()
    {
        await using var other = new RouteContext();
        var otherRules = new PricingRules();
        other.AddToRegistry("rules", otherRules);
        var expression = new StringExpression("${bean:rules.Gross(1, 1)}");

        expression.Evaluate<object>(Message());
        expression.Evaluate<object>(new Exchange(new Message("x")) { Context = other });

        _rules.Calls.Should().Be(1);
        otherRules.Calls.Should().Be(1);
    }

    [Fact]
    public async Task InACSharpRoute_FilterAndSetBody()
    {
        var seen = new List<object?>();
        _context.AddRoutes(r => r.From("direct://bean-fn")
            .Filter("bean:rules.IsGold(header.name)")
                .SetBody(new StringExpression("${bean:rules.Gross(header.qty, 2)}"))
                .Process(e => seen.Add(e.In.Body))
            .EndFilter());
        await _context.Start();
        var producer = _context.GetEndpoint("direct://bean-fn").CreateProducer();
        await producer.Start();

        foreach (var name in new[] { "ann", "bob" })
        {
            var exchange = new Exchange(new Message("in"));
            exchange.In.Headers["name"] = name;
            exchange.In.Headers["qty"] = 5;
            await producer.Process(exchange);
        }

        seen.Should().Equal(10.0);
    }
}
