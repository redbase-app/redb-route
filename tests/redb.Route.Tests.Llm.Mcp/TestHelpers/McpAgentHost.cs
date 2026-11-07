using Microsoft.Extensions.DependencyInjection;
using redb.Route.Llm.Engine.Governance;
using redb.Route.Llm.Engine.Observability;
using redb.Route.Llm.Mcp.Protocol;

namespace redb.Route.Tests.Llm.Mcp.TestHelpers;

/// <summary>
/// Minimal LLM-host wiring for MCP integration tests. Mirrors the
/// <c>LiveLlmHost</c> pattern from <c>redb.Route.Tests.Llm</c> but adds the
/// <see cref="McpComponent"/> + <see cref="IMcpRegistry"/> so the agent can
/// dispatch <c>mcp://</c> URIs.
/// <para>
/// Tests inject pre-discovered <see cref="IMcpClient"/> instances (typically
/// from <see cref="SerenaFixture"/>) and supply the matching <see cref="ToolDefinition"/>
/// list — descriptors are projected into <see cref="IToolDescriptorRegistry"/>
/// the same way <see cref="McpDiscoveryService"/> would.
/// </para>
/// </summary>
public sealed class McpAgentHost : IAsyncDisposable
{
    /// <summary>The shared route context.</summary>
    public RouteContext Context { get; }
    /// <summary>The producer template — used to invoke <c>direct:</c> entrypoints.</summary>
    public IProducerTemplate ProducerTemplate { get; }
    /// <summary>The agent engine wired with the producer template + observer.</summary>
    public IAgentEngine Engine { get; }
    /// <summary>The tool registry into which MCP descriptors were projected.</summary>
    public IToolDescriptorRegistry ToolRegistry { get; }
    /// <summary>The MCP client registry the producer dispatches against.</summary>
    public IMcpRegistry McpRegistry { get; }

    private McpAgentHost(
        RouteContext ctx, IProducerTemplate pt, IAgentEngine engine,
        IToolDescriptorRegistry toolRegistry, IMcpRegistry mcpRegistry)
    {
        Context = ctx;
        ProducerTemplate = pt;
        Engine = engine;
        ToolRegistry = toolRegistry;
        McpRegistry = mcpRegistry;
    }

    /// <summary>Builds a host with the LLM + MCP components wired and the supplied client(s) registered.</summary>
    /// <param name="serverName">Logical MCP server name (matches the <c>mcp://&lt;serverName&gt;/...</c> URI).</param>
    /// <param name="client">Pre-initialized MCP client (e.g. from <see cref="SerenaFixture"/>).</param>
    /// <param name="tools">Tool catalogue to project as <see cref="McpToolDescriptor"/> instances.</param>
    /// <param name="observer">Optional agent observer — defaults to <see cref="NoopAgentObserver"/>.</param>
    public static McpAgentHost Build(
        string serverName,
        IMcpClient client,
        IReadOnlyList<ToolDefinition> tools,
        IAgentObserver? observer = null)
    {
        RouteContext ctx = null!;
        var services = new ServiceCollection();
        services.AddSingleton<IRouteContext>(_ => ctx);
        var sp = services.BuildServiceProvider();

        ctx = new RouteContext(sp, contextId: $"mcp-test-{serverName}");

        ctx.AddComponent(new LlmComponent());

        var mcpRegistry = new McpRegistry();
        mcpRegistry.Register(client);
        ctx.AddComponent(new McpComponent(mcpRegistry));

        var toolRegistry = new ToolDescriptorRegistry();
        foreach (var tool in tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Name)) continue;

            var modelName = McpToolDescriptor.BuildModelFacingName(serverName, tool.Name);
            var capability = new LlmToolCapability
            {
                Name = modelName,
                Description = tool.Description ?? $"MCP tool '{tool.Name}' from server '{serverName}'.",
                InputSchema = McpToolDescriptor.BuildInputSchema(tool),
                Safety = new LlmToolSafety(),
            };
            toolRegistry.Register(new McpToolDescriptor(serverName, tool.Name, capability));
        }
        ctx.AddService(typeof(IToolDescriptorRegistry), toolRegistry);

        var pt = new ProducerTemplate(ctx);
        ctx.AddService(typeof(IProducerTemplate), pt);

        var engine = new AgentEngine(
            logger: null,
            producerTemplate: pt,
            observer: observer ?? new NoopAgentObserver(),
            budget: new NoopBudgetEnforcer(),
            approval: new AutoApproveGate(),
            redaction: new NoopRedactionFilter(),
            shadow: new NoopShadowRunner(),
            conversation: null,
            idempotency: null,
            approvalStore: null);
        ctx.AddService(typeof(IAgentEngine), engine);

        return new McpAgentHost(ctx, pt, engine, toolRegistry, mcpRegistry);
    }

    /// <summary>Registers a connection factory for use in routes.</summary>
    public McpAgentHost AddFactory(string name, LlmConnectionFactory factory)
    {
        factory.Name = name;
        Context.AddToRegistry(name, factory);
        return this;
    }

    /// <summary>Declares routes inline and starts the context + producer template.</summary>
    public async Task<McpAgentHost> StartAsync(Action<InlineRouteBuilder> configure)
    {
        Context.AddRoutes(configure);
        await Context.Start().ConfigureAwait(false);
        ((ProducerTemplate)ProducerTemplate).Start();
        return this;
    }

    /// <summary>Sends one message into <paramref name="endpointUri"/> and returns the resulting exchange.</summary>
    public async Task<IExchange> SendAsync(string endpointUri, string body, CancellationToken ct = default)
    {
        var endpoint = Context.GetEndpoint(endpointUri);
        var producer = endpoint.CreateProducer();
        await producer.Start(ct).ConfigureAwait(false);

        var msg = new Message(body);
        var ex = Exchange.Create(msg, endpoint.ScopeFactory);
        await producer.Process(ex, ct).ConfigureAwait(false);
        return ex;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync().ConfigureAwait(false);
        if (ProducerTemplate is IDisposable d) d.Dispose();
    }
}
