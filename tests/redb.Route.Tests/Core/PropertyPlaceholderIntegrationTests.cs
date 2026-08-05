using FluentAssertions;
using Microsoft.Extensions.Configuration;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// End-to-end tests for property placeholders through the full DSL → Compiler → Engine pipeline:
/// a live route whose <c>From</c> and <c>To</c> URIs carry <c>{{key}}</c> placeholders, started and
/// driven with a real message, asserting the message actually reaches the resolved destination.
/// Values are drawn from IConfiguration, context properties and inline defaults.
/// </summary>
public class PropertyPlaceholderIntegrationTests : IAsyncDisposable
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
    public async Task Placeholders_InFromAndTo_FromConfiguration_DeliverEndToEnd()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["orders.source"] = "direct://cfg-in",
                ["orders.dest"] = "direct://cfg-out",
            })
            .Build();
        _context.AddService(typeof(IConfiguration), config);

        var received = new List<string?>();
        _context.AddRoutes(r =>
        {
            r.From("direct://cfg-out").Process(e => received.Add(e.In.Body?.ToString()));

            // Both endpoints of the working route are placeholders resolved from IConfiguration.
            r.From("{{orders.source}}").To("{{orders.dest}}");
        });

        // The consumer is reachable through the resolved literal URI — proof the placeholder From bound it.
        var producer = await StartAndGetProducer("direct://cfg-in");
        await producer.Process(new Exchange(new Message("order-42")));

        received.Should().ContainSingle().Which.Should().Be("order-42");
    }

    [Fact]
    public async Task Placeholder_DefaultValue_UsedInLiveRoute()
    {
        var received = new List<string?>();
        _context.AddRoutes(r =>
        {
            r.From("direct://def-out").Process(e => received.Add(e.In.Body?.ToString()));

            // The key is not configured anywhere → the inline default drives the destination.
            r.From("direct://def-in").To("direct://{{unset.dest:def-out}}");
        });

        var producer = await StartAndGetProducer("direct://def-in");
        await producer.Process(new Exchange(new Message("payload")));

        received.Should().ContainSingle().Which.Should().Be("payload");
    }

    [Fact]
    public async Task Placeholder_FromContextProperty_DeliversEndToEnd()
    {
        // Container-free path: values come from context.SetProperty, no IConfiguration registered.
        _context.SetProperty("dest.queue", "prop-out");

        var received = new List<string?>();
        _context.AddRoutes(r =>
        {
            r.From("direct://prop-out").Process(e => received.Add(e.In.Body?.ToString()));
            r.From("direct://prop-in").To("direct://{{dest.queue}}");
        });

        var producer = await StartAndGetProducer("direct://prop-in");
        await producer.Process(new Exchange(new Message("hi")));

        received.Should().ContainSingle().Which.Should().Be("hi");
    }
}
