using redb.Route.Tests.Llm.TestHelpers;
using LlmDsl = redb.Route.Llm.Fluent.Llm;

namespace redb.Route.Tests.Llm;

/// <summary>
/// End-to-end tests for what a tool route actually receives when the agent
/// dispatches a tool call. Guards the fix for "tool exchange built from scratch":
/// the tool now runs on a linked child of the agent exchange, so properties,
/// route id and the parent's DI scope come across, while headers stay filtered
/// by <see cref="ToolHeaderPolicy"/>.
/// </summary>
public sealed class ToolExchangeContextTests
{
    private static EchoToolRoute EchoTool() => new(
        toolName: "echo",
        description: "echo back the result",
        inputSchema: """{"type":"object","properties":{"v":{"type":"number"}}}""",
        replyJson: """{"r":42}""");

    private static FakeProvider ToolThenText() => new FakeProvider()
        .EnqueueToolUse("echo", "{\"v\":1}")
        .EnqueueText("done");

    private static async Task<LiveLlmHost> HostAsync(FakeProvider fake, EchoToolRoute tool, string llmUri)
    {
        var host = LiveLlmHost.Build();
        host.Context.AddToRegistry("fake", new LlmConnectionFactory
        {
            Name = "fake",
            Provider = "fake",
            ModelId = fake.ModelId,
            PrebuiltProvider = fake
        });

        await host.StartAsync(tool, r => r.From("direct:agent").To(llmUri).To("mock:done"));
        return host;
    }

    [Fact]
    public async Task ToolExchange_InheritsParentPropertiesAndRouteId()
    {
        var fake = ToolThenText();
        var tool = EchoTool();
        // ?redb=my-llm-db lands in exchange.Properties[LlmKeys.RedbName] — the
        // canonical example of state a tool needs and used to lose.
        await using var host = await HostAsync(fake, tool,
            LlmDsl.Factory("fake").Tools("echo").AsUri() + "&redb=my-llm-db");

        await host.SendAsync("direct:agent", "go");

        tool.CapturedProperties.Should().ContainSingle();
        tool.CapturedProperties[0].Should().ContainKey(LlmKeys.RedbName)
            .WhoseValue.Should().Be("my-llm-db");
        tool.CapturedRouteIds[0].Should().NotBeNull();
    }

    [Fact]
    public async Task ToolExchange_SharesTheAgentDiScope()
    {
        var fake = ToolThenText();
        var tool = EchoTool();

        IServiceProvider? agentScope = null;

        var host = LiveLlmHost.Build();
        host.Context.AddToRegistry("fake", new LlmConnectionFactory
        {
            Name = "fake",
            Provider = "fake",
            ModelId = fake.ModelId,
            PrebuiltProvider = fake
        });

        await using var _ = host;
        await host.StartAsync(tool, r => r
            .From("direct:agent")
            .Process(e => agentScope = e.ServiceProvider)
            .To(LlmDsl.Factory("fake").Tools("echo").AsUri())
            .To("mock:done"));

        await host.SendAsync("direct:agent", "go");

        agentScope.Should().NotBeNull("the test host wires a scope factory onto the inbound exchange");
        tool.CapturedServiceProviders.Should().ContainSingle();
        // Same instance, not merely non-null: scoped services the conversation
        // resolved (principal, tenant accessor, per-exchange IRedbService) must be
        // the very ones the tool sees.
        tool.CapturedServiceProviders[0].Should().BeSameAs(agentScope);
    }

    [Fact]
    public async Task ToolExchange_CarriesResolvedPrincipalAndAuditTags()
    {
        var fake = ToolThenText();
        var tool = EchoTool();
        // ?user= is an expression over the inbound exchange — the resolved value,
        // not the raw llm.user.id header, is what must reach the tool.
        await using var host = await HostAsync(fake, tool,
            LlmDsl.Factory("fake")
                .Tools("echo")
                .User("${header.X-User-Id}")
                .Audit("tier", "gold")
                .AsUri());

        await host.SendAsync("direct:agent", "go", new Dictionary<string, object?>
        {
            ["X-User-Id"] = "alice@example.com"
        });

        var headers = tool.CapturedHeaders.Should().ContainSingle().Subject;
        headers[LlmHeaders.UserId].Should().Be("alice@example.com");
        headers[LlmHeaders.AuditTagPrefix + "tier"].Should().Be("gold");
    }

    [Fact]
    public async Task ToolExchange_DoesNotLeakInboundTransportHeaders()
    {
        var fake = ToolThenText();
        var tool = EchoTool();
        await using var host = await HostAsync(fake, tool, LlmDsl.Factory("fake").Tools("echo").AsUri());

        await host.SendAsync("direct:agent", "go", new Dictionary<string, object?>
        {
            ["Authorization"] = "Bearer secret",
            ["Cookie"] = "session=abc",
            ["x-tenant-id"] = "acme"
        });

        var headers = tool.CapturedHeaders.Should().ContainSingle().Subject;
        headers.Should().NotContainKey("Authorization");
        headers.Should().NotContainKey("Cookie");
        headers.Should().NotContainKey("x-tenant-id");
        // The engine's own stamps are always there.
        headers[LlmHeaders.ToolName].Should().Be("echo");
    }

    [Fact]
    public async Task PropagateToolHeaders_OptsNamedHeadersIn()
    {
        var fake = ToolThenText();
        var tool = EchoTool();
        await using var host = await HostAsync(fake, tool,
            LlmDsl.Factory("fake")
                .Tools("echo")
                .PropagateToolHeaders("x-tenant-id", "x-app-*")
                .AsUri());

        await host.SendAsync("direct:agent", "go", new Dictionary<string, object?>
        {
            ["x-tenant-id"] = "acme",
            ["x-app-locale"] = "ru-RU",
            ["Authorization"] = "Bearer secret"
        });

        var headers = tool.CapturedHeaders.Should().ContainSingle().Subject;
        headers["x-tenant-id"].Should().Be("acme");
        headers["x-app-locale"].Should().Be("ru-RU");
        headers.Should().NotContainKey("Authorization", "opt-in is per name, not a blanket open");
    }

    [Fact]
    public async Task ToolCall_StillReturnsResultToTheModel()
    {
        // Regression guard for the dispatch rewrite itself (RequestBody → RequestAsync
        // on a caller-owned exchange): the reply must still round-trip.
        var fake = ToolThenText();
        var tool = EchoTool();
        await using var host = await HostAsync(fake, tool, LlmDsl.Factory("fake").Tools("echo").AsUri());

        await host.SendAsync("direct:agent", "go");

        tool.CapturedInputs.Should().ContainSingle().Which.Should().Contain("\"v\":1");
        fake.CapturedRequests[^1].Messages
            .SelectMany(m => m.Content)
            .OfType<LlmToolResultBlock>()
            .Should().ContainSingle()
            .Which.OutputJson.Should().Contain("42");
    }

    [Fact]
    public async Task AgentExchange_KeepsItsScopeAfterTheToolCall()
    {
        // The linked child must not dispose the shared parent scope on the way out —
        // otherwise everything after the tool call runs on a disposed provider.
        var fake = ToolThenText();
        var tool = EchoTool();
        await using var host = await HostAsync(fake, tool, LlmDsl.Factory("fake").Tools("echo").AsUri());

        var ex = await host.SendAsync("direct:agent", "go");

        ex.ServiceProvider.Should().NotBeNull();
        // Resolving after the run proves the scope is still alive, not disposed.
        var act = () => ex.ServiceProvider!.GetService(typeof(IRouteContext));
        act.Should().NotThrow();
    }
}
