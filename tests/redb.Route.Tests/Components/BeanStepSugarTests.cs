using AwesomeAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;

namespace redb.Route.Tests.Components;

/// <summary>
/// <c>.Bean(reference, method)</c>, Camel's <c>bean(ref, method)</c>: sugar over the <c>bean:</c> URI. It adds the
/// same <see cref="ToDefinition"/> that <c>To("bean:...")</c> adds, so both meet at one endpoint; the older forms
/// (<c>To("bean:")</c>, <c>.Bean&lt;T&gt;(lambda)</c>) are untouched.
/// </summary>
public class BeanStepSugarTests : IAsyncLifetime
{
    private readonly RouteContext _context = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private string[] ToUrisOf(Action<InlineRouteBuilder> routes)
    {
        _context.AddRoutes(routes);
        var builder = _context.RouteBuilders.Last();
        if (!builder.IsBuilt)
            builder.InternalBuild(_context);
        return builder.Definitions.Single().Outputs.OfType<ToDefinition>().Select(t => t.Uri).ToArray();
    }

    private async Task<IProducer> StartAndProducer(string fromUri)
    {
        await _context.Start();
        var producer = _context.GetEndpoint(fromUri).CreateProducer();
        await producer.Start();
        return producer;
    }

    [Theory]
    [InlineData("#greeter", "Greet", "bean:#greeter?method=Greet")]
    [InlineData("#greeter", null, "bean:#greeter")]
    [InlineData("Acme.Orders.Pricing, Acme.Orders", "Gross(${header.qty}, ${header.price})",
        "bean:Acme.Orders.Pricing, Acme.Orders?method=Gross(${header.qty}, ${header.price})")]
    public void Bean_AddsTheToDefinitionOfTheBeanUri(string reference, string? method, string expected)
    {
        ToUrisOf(r => r.From("direct://sugar").Bean(reference, method)).Should().Equal(expected);
    }

    [Fact]
    public void Bean_AndTo_YieldTheSameUri()
    {
        var sugar = ToUrisOf(r => r.From("direct://sugar-a").Bean("#greeter", "Greet"));
        var uri = ToUrisOf(r => r.From("direct://sugar-b").To("bean:#greeter?method=Greet"));

        sugar.Should().Equal(uri);
    }

    [Fact]
    public void Bean_EncodesOnlyTheCharactersTheUriParseWouldBreak()
    {
        ToUrisOf(r => r.From("direct://sugar-amp").Bean("#rules", "Check(${header.a} AND 5%)"))
            .Should().Equal("bean:#rules?method=Check(${header.a} AND 5%25)");
        ToUrisOf(r => r.From("direct://sugar-amp2").Bean("#rules", "Check('a&b')"))
            .Should().Equal("bean:#rules?method=Check('a%26b')");
    }

    [Theory]
    [InlineData("Gross(${header.a} AND ${header.b})", "bean:#rules?method=Gross(${header.a} AND ${header.b})")]
    [InlineData("Check('a&b')", "bean:#rules?method=Check('a%26b')")]
    [InlineData("Check('5%', ${body})", "bean:#rules?method=Check('5%25', ${body})")]
    public void Bean_MethodArrivesAtTheEndpointAsWritten_AsTheHandWrittenUriDoes(string method, string handWritten)
    {
        // The engine reads method= from the parsed URI: the sugar and the hand-written form must parse alike,
        // and the value the bean binding sees is the method exactly as the caller wrote it.
        var sugar = EndpointUriParser.Parse(Route.Components.Bean.BeanComponent.UriFor("#rules", method));
        var manual = EndpointUriParser.Parse(handWritten);

        sugar.NormalizedKey.Should().Be(manual.NormalizedKey);
        sugar.RawParameters["method"].Should().Be(method);
        manual.RawParameters["method"].Should().Be(method);
    }

    [Fact]
    public void Bean_ReferenceWithAQuery_IsRefused()
    {
        var act = () => ToUrisOf(r => r.From("direct://sugar-q").Bean("#greeter?method=Greet"));

        act.Should().Throw<ArgumentException>().WithMessage("*contains '?'*second argument*");
    }

    [Fact]
    public async Task Bean_CallsTheRegisteredObject_AndTheResultBecomesTheBody()
    {
        _context.AddToRegistry("greeter", new GreeterBean { Prefix = "sugar" });
        _context.AddRoutes(r => r.From("direct://sugar-run").Bean("#greeter", "Greet"));
        var producer = await StartAndProducer("direct://sugar-run");

        var exchange = new Exchange(new Message("bob"));
        await producer.Process(exchange);

        exchange.In.Body.Should().Be("sugar:bob");
    }

    [Fact]
    public async Task Bean_InsideAScope_KeepsTheScopeType()
    {
        // The step returns the scope, as To does: EndFilter() must still be reachable after it.
        _context.AddToRegistry("greeter", new GreeterBean { Prefix = "scoped" });
        _context.AddRoutes(r => r.From("direct://sugar-scope")
            .Filter("header.go == 'yes'")
                .Bean("#greeter", "Greet")
            .EndFilter());
        var producer = await StartAndProducer("direct://sugar-scope");

        var pass = new Exchange(new Message("in"));
        pass.In.Headers["go"] = "yes";
        await producer.Process(pass);
        var skip = new Exchange(new Message("out"));
        skip.In.Headers["go"] = "no";
        await producer.Process(skip);

        pass.In.Body.Should().Be("scoped:in");
        skip.In.Body.Should().Be("out");
    }
}
