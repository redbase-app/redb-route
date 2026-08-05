using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>
/// Integration tests for the Routing Slip EIP through the DSL → Compiler → Engine pipeline,
/// using real in-process <c>direct://</c> endpoints.
/// </summary>
public class RoutingSlipIntegrationTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private async Task<IProducer> StartAndGetProducer(string fromUri)
    {
        await _context.Start();
        var producer = _context.GetEndpoint(fromUri).CreateProducer();
        await producer.Start();
        return producer;
    }

    [Fact]
    public async Task RoutingSlip_StringSlip_PipesThroughEndpoints_InOrder()
    {
        var visited = new List<string>();

        _context.AddRoutes(r =>
        {
            r.From("direct://slip-a").Process(_ => visited.Add("a"));
            r.From("direct://slip-b").Process(_ => visited.Add("b"));
            r.From("direct://slip-c").Process(_ => visited.Add("c"));

            r.From("direct://slip-in")
                .RoutingSlip("direct://slip-a,direct://slip-b,direct://slip-c");
        });

        var producer = await StartAndGetProducer("direct://slip-in");
        await producer.Process(new Exchange(new Message("data")));

        visited.Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task RoutingSlip_ChainsBodyAlongSlip_OutToIn()
    {
        _context.AddRoutes(r =>
        {
            r.From("direct://chain-a").Process(e => e.In.Body = e.In.Body?.ToString() + ":a");
            r.From("direct://chain-b").Process(e => e.In.Body = e.In.Body?.ToString() + ":b");

            r.From("direct://chain-in")
                .RoutingSlip("direct://chain-a,direct://chain-b");
        });

        var producer = await StartAndGetProducer("direct://chain-in");
        var exchange = new Exchange(new Message("start"));
        await producer.Process(exchange);

        exchange.In.Body.Should().Be("start:a:b");
    }

    [Fact]
    public async Task RoutingSlip_FromHeaderTemplate_ResolvesSlipPerMessage()
    {
        var visited = new List<string>();

        _context.AddRoutes(r =>
        {
            r.From("direct://h-a").Process(_ => visited.Add("a"));
            r.From("direct://h-b").Process(_ => visited.Add("b"));

            // The slip is taken from a header, resolved per message.
            r.From("direct://h-in").RoutingSlip("${header.mySlip}");
        });

        var producer = await StartAndGetProducer("direct://h-in");
        var exchange = new Exchange(new Message("data"));
        exchange.In.Headers["mySlip"] = "direct://h-a,direct://h-b";
        await producer.Process(exchange);

        visited.Should().Equal("a", "b");
    }

    [Fact]
    public async Task RoutingSlip_FactoryOverload_ComputesSlipFromExchange()
    {
        var visited = new List<string>();

        _context.AddRoutes(r =>
        {
            r.From("direct://f-a").Process(_ => visited.Add("a"));
            r.From("direct://f-b").Process(_ => visited.Add("b"));

            r.From("direct://f-in").RoutingSlip(ex =>
                ((string)ex.In.Body!).Split('|'));
        });

        var producer = await StartAndGetProducer("direct://f-in");
        await producer.Process(new Exchange(new Message("direct://f-a|direct://f-b")));

        visited.Should().Equal("a", "b");
    }

    [Fact]
    public async Task RoutingSlip_IgnoreInvalidEndpoints_SkipsUnknown()
    {
        var visited = new List<string>();

        _context.AddRoutes(r =>
        {
            r.From("direct://i-a").Process(_ => visited.Add("a"));
            r.From("direct://i-c").Process(_ => visited.Add("c"));

            // "bogus://" has no registered component, so it fails endpoint resolution — exactly what
            // ignoreInvalidEndpoints skips (Camel semantics: invalid = unresolvable, not a runtime error).
            r.From("direct://i-in").RoutingSlip(
                "direct://i-a,bogus://missing,direct://i-c",
                uriDelimiter: ",",
                ignoreInvalidEndpoints: true);
        });

        var producer = await StartAndGetProducer("direct://i-in");
        await producer.Process(new Exchange(new Message("data")));

        visited.Should().Equal("a", "c");
    }
}
