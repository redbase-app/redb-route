using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using redb.Route.Llm.Mcp.Protocol;

namespace redb.Route.Tests.Llm.Mcp;

/// <summary>
/// End-to-end tests against a real Serena MCP server through the
/// <see cref="McpProducer"/>. No LLM involvement — we drive
/// <c>mcp://serena/&lt;tool&gt;</c> directly via the producer (and once via
/// <see cref="IProducerTemplate"/> to exercise the new CT-aware overload).
/// </summary>
[Trait("Category", "LiveMcp")]
[Collection("SerenaSerial")]
public sealed class SerenaProducerTests
{
    private readonly SerenaFixture _fixture;

    public SerenaProducerTests(SerenaFixture fixture) => _fixture = fixture;

    [SerenaFact]
    public async Task ListsTools_AndExposesGetSymbolsOverview()
    {
        _fixture.Client.Should().NotBeNull("fixture must have initialized Serena");
        _fixture.Tools.Should().NotBeEmpty("Serena reports a non-empty tool catalogue");

        var names = _fixture.Tools.Select(t => t.Name).ToList();
        names.Should().Contain(n => n.Contains("get_symbols_overview", StringComparison.OrdinalIgnoreCase));
        names.Should().Contain(n => n.Contains("find_symbol", StringComparison.OrdinalIgnoreCase));
    }

    [SerenaFact]
    public async Task DirectProducer_CallsTool_ReturnsContentAndHeaders()
    {
        var registry = new McpRegistry();
        registry.Register(_fixture.Client!);

        var component = new McpComponent(registry);
        var endpoint = (McpEndpoint)component.CreateEndpoint(
            EndpointUriParser.Parse("mcp://serena/get_symbols_overview"));

        var producer = (McpProducer)endpoint.CreateProducer();

        var args = new JsonObject
        {
            ["relative_path"] = "redb.Route/src/redb.Route.Llm.Mcp/Protocol/McpProtocol.cs",
        };
        var msg = new Message(args.ToJsonString());
        var exchange = new Exchange(msg);

        await producer.Process(exchange, CancellationToken.None);

        var body = (string)exchange.In.Body!;
        body.Should().NotBeNullOrWhiteSpace();

        // Every Serena response is a JSON array of content blocks.
        var arr = JsonNode.Parse(body) as JsonArray;
        arr.Should().NotBeNull("MCP tools/call returns a content[] array");
        arr!.Count.Should().BeGreaterThan(0);

        exchange.In.Headers.TryGetValue("Mcp-Server", out var server);
        server.Should().Be("serena");
        exchange.In.Headers.TryGetValue("Mcp-Tool", out var tool);
        tool.Should().Be("get_symbols_overview");
    }

    [SerenaFact]
    public async Task ViaProducerTemplate_CallsTool_AndReturnsString()
    {
        var registry = new McpRegistry();
        registry.Register(_fixture.Client!);

        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();
        var ctx = new RouteContext(sp, contextId: "mcp-producer-test");
        ctx.AddComponent(new McpComponent(registry));
        await using var ctxScope = ctx;
        await ctx.Start();

        using var pt = new ProducerTemplate(ctx);
        pt.Start();

        var args = new JsonObject
        {
            ["relative_path"] = "redb.Route/src/redb.Route.Llm.Mcp/McpProducer.cs",
        };

        var result = await pt.RequestBody(
            "mcp://serena/get_symbols_overview",
            args.ToJsonString(),
            CancellationToken.None);

        result.Should().NotBeNull();
        var json = result!.ToString();
        json.Should().NotBeNullOrWhiteSpace();
        JsonNode.Parse(json!).Should().NotBeNull("response must be valid JSON");
    }

    [SerenaFact]
    public async Task DirectProducer_RespectsCancellationToken()
    {
        var registry = new McpRegistry();
        registry.Register(_fixture.Client!);

        var component = new McpComponent(registry);
        var endpoint = (McpEndpoint)component.CreateEndpoint(
            EndpointUriParser.Parse("mcp://serena/find_symbol"));
        var producer = (McpProducer)endpoint.CreateProducer();

        // A find_symbol with broad query → should run long enough that we can cancel it.
        var args = new JsonObject
        {
            ["name_path"] = "Mcp",
            ["substring_matching"] = true,
            ["relative_path"] = "",
        };
        var exchange = new Exchange(new Message(args.ToJsonString()));

        using var cts = new CancellationTokenSource();
        var task = producer.Process(exchange, cts.Token);

        // Cancel immediately — the producer/client must propagate.
        cts.Cancel();

        var act = async () => await task;
        var thrown = await act.Should().ThrowAsync<OperationCanceledException>();
        thrown.Which.CancellationToken.IsCancellationRequested.Should().BeTrue();
    }

    [SerenaFact]
    public async Task DirectProducer_DeadClient_ThrowsMcpException()
    {
        // Build a registry whose client we mark "Dead" by disposing it.
        var deadClient = new redb.Route.Llm.Mcp.Transport.StdioMcpClient(
            "deadhorse",
            McpTransport.Stdio("uvx", new[] { "--version" }),
            NullLogger.Instance);
        await deadClient.DisposeAsync();

        var registry = new McpRegistry();
        registry.Register(deadClient);

        var component = new McpComponent(registry);
        var endpoint = (McpEndpoint)component.CreateEndpoint(
            EndpointUriParser.Parse("mcp://deadhorse/anything"));
        var producer = (McpProducer)endpoint.CreateProducer();

        var exchange = new Exchange(new Message("{}"));

        var act = async () => await producer.Process(exchange);
        await act.Should().ThrowAsync<McpException>().WithMessage("*deadhorse*dead*");
    }
}
