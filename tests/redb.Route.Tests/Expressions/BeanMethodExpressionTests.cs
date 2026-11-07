using AwesomeAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Predicates;

namespace redb.Route.Tests.Expressions;

/// <summary>A bean whose conditions and values <c>Method()</c> calls, sync and async.</summary>
public class MembershipRules
{
    public bool IsGold(string? name) => name == "ann";

    public double Gross(int qty, double price) => qty * price;

    public async Task<bool> IsGoldAsync(string? name)
    {
        await Task.Yield();
        return name == "ann";
    }
}

/// <summary>
/// <c>Method(reference, spec)</c>, Camel's <c>method(ref, "name")</c> (stage 2 step f): a registry bean's method as an
/// expression and a condition, through the binding and resolution of the <c>bean:</c> function; in a condition a
/// <c>Task&lt;bool&gt;</c> method is awaited, in a value position the call is synchronous.
/// </summary>
public sealed class BeanMethodExpressionTests : IAsyncLifetime
{
    private readonly RouteContext _context = new();

    public Task InitializeAsync()
    {
        _context.AddToRegistry("rules", new MembershipRules());
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _context.DisposeAsync();

    private Exchange Message(params (string Key, object? Value)[] headers)
    {
        var exchange = new Exchange(new Message("in")) { Context = _context };
        foreach (var (key, value) in headers)
            exchange.In.Headers[key] = value;
        return exchange;
    }

    private async Task<List<string>> Run(Action<InlineRouteBuilder> routes, string from, params string[] names)
    {
        var seen = new List<string>();
        _context.AddToRegistry("seen", seen);
        _context.AddRoutes(routes);
        await _context.Start();
        var producer = _context.GetEndpoint(from).CreateProducer();
        await producer.Start();
        foreach (var name in names)
        {
            var exchange = new Exchange(new Message(name));
            exchange.In.Headers["name"] = name;
            exchange.In.Headers["qty"] = 4;
            await producer.Process(exchange);
            if (exchange.Properties.TryGetValue("hit", out var hit))
                seen.Add($"{name}:{hit}");
        }
        return seen;
    }

    [Fact]
    public async Task Filter_TakesMethod_WithoutAmbiguity()
    {
        var seen = await Run(r => r.From("direct://m-filter")
            .Filter(RouteBuilder.Method("#rules", "IsGold(${header.name})"))
                .Process(e => e.Properties["hit"] = "gold")
            .EndFilter(), "direct://m-filter", "ann", "bob");

        seen.Should().Equal("ann:gold");
    }

    [Fact]
    public async Task Filter_AwaitsATaskReturningCondition()
    {
        var seen = await Run(r => r.From("direct://m-async")
            .Filter(RouteBuilder.Method("rules", "IsGoldAsync(${header.name})"))
                .Process(e => e.Properties["hit"] = "gold")
            .EndFilter(), "direct://m-async", "ann", "bob");

        seen.Should().Equal("ann:gold");
    }

    [Fact]
    public async Task ChoiceWhen_TakesMethod()
    {
        var seen = await Run(r => r.From("direct://m-choice")
            .Choice()
                .When(RouteBuilder.Method("#rules", "IsGold(${header.name})")).Process(e => e.Properties["hit"] = "gold")
                .Otherwise().Process(e => e.Properties["hit"] = "std")
            .EndChoice(), "direct://m-choice", "ann", "bob");

        seen.Should().Equal("ann:gold", "bob:std");
    }

    private async Task<List<string>> Validated(Func<IRouteDefinition, IRouteDefinition> validate, string from)
    {
        var outcomes = new List<string>();
        _context.AddRoutes(r => validate(r.From(from)).Process(e => e.Properties["hit"] = "valid"));
        await _context.Start();
        var producer = _context.GetEndpoint(from).CreateProducer();
        await producer.Start();
        foreach (var name in new[] { "ann", "bob" })
        {
            var exchange = new Exchange(new Message(name));
            exchange.In.Headers["name"] = name;
            string? error = null;
            try
            {
                await producer.Process(exchange);
            }
            catch (redb.Route.Validation.ValidationException ex)
            {
                error = ex.Message;
            }
            outcomes.Add($"{name}:{(exchange.Properties.ContainsKey("hit") ? "valid" : error ?? exchange.Exception?.Message ?? "stopped")}");
        }
        return outcomes;
    }

    [Fact]
    public async Task Validate_TakesMethod_AndAwaitsATaskReturningCondition()
    {
        var outcomes = await Validated(r => r.Validate(RouteBuilder.Method("#rules", "IsGoldAsync(${header.name})"), "not gold"),
            "direct://m-validate-async");

        outcomes[0].Should().Be("ann:valid");
        outcomes[1].Should().StartWith("bob:").And.Contain("not gold");
    }

    [Fact]
    public async Task Validate_TakesAnExpressionCondition()
    {
        var outcomes = await Validated(r => r.Validate(RouteBuilder.Expr("header.name == 'ann'"), "not ann"),
            "direct://m-validate-expr");

        outcomes[0].Should().Be("ann:valid");
        outcomes[1].Should().StartWith("bob:").And.Contain("not ann");
    }

    [Fact]
    public async Task Validate_WithAPredicate_IsUnchanged()
    {
        var outcomes = await Validated(r => r.Validate(PredicateFactory.FromString("header.name == 'ann'"), "not ann"),
            "direct://m-validate-predicate");

        outcomes[0].Should().Be("ann:valid");
        outcomes[1].Should().StartWith("bob:").And.Contain("not ann");
    }

    [Fact]
    public async Task SetBody_TakesTheValue_WithItsType()
    {
        object? body = null;
        _context.AddRoutes(r => r.From("direct://m-value")
            .SetBody(RouteBuilder.Method("#rules", "Gross(${header.qty}, 2.5)"))
            .Process(e => body = e.In.Body));
        await _context.Start();
        var producer = _context.GetEndpoint("direct://m-value").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("x"));
        exchange.In.Headers["qty"] = 4;
        await producer.Process(exchange);

        body.Should().Be(10.0);
    }

    [Fact]
    public void AValuePosition_RefusesATaskReturningMethod_AsTheFunctionDoes()
    {
        var expression = RouteBuilder.Method("#rules", "IsGoldAsync(${header.name})");

        expression.Invoking(e => e.Evaluate<object>(Message(("name", "ann"))))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("bean:rules.IsGoldAsync returns Task`1: a bean: call in an expression is synchronous*");
        expression.Invoking(e => e.Matches(Message(("name", "ann"))))
            .Should().Throw<InvalidOperationException>().WithMessage("*synchronous*");
    }

    [Fact]
    public async Task TheConditionPath_IsTheAsyncPredicate()
    {
        var predicate = PredicateFactory.FromExpression(RouteBuilder.Method("#rules", "IsGoldAsync(${header.name})"));

        (await predicate.MatchesAsync(Message(("name", "ann")))).Should().BeTrue();
        (await predicate.MatchesAsync(Message(("name", "bob")))).Should().BeFalse();
    }

    [Theory]
    [InlineData("Acme.Rules, Acme")]
    [InlineData("Acme.Rules")]
    public void TheTypeForm_IsRefused(string reference)
    {
        var act = () => RouteBuilder.Method(reference, "IsGold(body)");

        act.Should().Throw<ArgumentException>().WithMessage("*in an expression the bean is a registry name*");
    }

    [Fact]
    public void ItsTemplate_IsTheSameCall()
    {
        var method = RouteBuilder.Method("#rules", "Gross(${header.qty}, 2.5)");

        method.ToTemplateString().Should().Be("${bean:rules.Gross(${header.qty}, 2.5)}");
        new StringExpression(method.ToTemplateString()).Evaluate<object>(Message(("qty", 2))).Should().Be(5.0);
    }

    [Fact]
    public void AMissingBeanOrMethod_IsTheCallsError()
    {
        RouteBuilder.Method("#absent", "IsGold(body)").Invoking(e => e.Evaluate<object>(Message()))
            .Should().Throw<InvalidOperationException>().WithMessage("bean:absent.IsGold: 'absent' is not in the context registry*");
        RouteBuilder.Method("#rules", "IsGold(body, body)").Invoking(e => e.Evaluate<object>(Message()))
            .Should().Throw<InvalidOperationException>().WithMessage("*'#rules' has no public method 'IsGold' taking 2 argument(s)*");
    }
}
