using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Engine.Governance;
using redb.Route.Llm.Engine.Observability;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Extensions;
using redb.Route.Processors;
using redb.Route.RedbCore.Extensions;
using redb.Route.Llm.Storage.Redb;
using redb.Route.Llm.Storage.Redb.Schemas;
using redb.Route.Tests.Llm.TestHelpers;
using LlmDsl = redb.Route.Llm.Fluent.Llm;

namespace redb.Route.Tests.Llm.Storage;

/// <summary>
/// The agent's memory in a named redb instance of its own: <c>?redb=</c> on the endpoint decides for everything the
/// endpoint writes, the tool-call audit and the accumulated cost of the conversation included, and nothing lands in the
/// default (business) instance. The stores are built as <c>AddRedbLlmStorage()</c> builds them, without a name.
/// </summary>
[Collection("StoragePro")]
public sealed class NamedRedbRoutingTests : IClassFixture<NamedRedbFixture>
{
    private readonly StorageProFixture _fx;
    private readonly NamedRedbFixture _named;

    public NamedRedbRoutingTests(StorageProFixture fx, NamedRedbFixture named)
    {
        _fx = fx;
        _named = named;
        named.AttachTo(fx.RouteContext);
    }

    private static EchoToolRoute EchoTool() => new(
        toolName: "echo",
        description: "echo back the result",
        inputSchema: """{"type":"object","properties":{"v":{"type":"number"}}}""",
        replyJson: """{"r":42}""");

    private async Task RunAsync(LiveLlmHost host, FakeProvider fake, string llmUri, string conversationId)
    {
        host.Context.AddToRegistry("fake", new LlmConnectionFactory
        {
            Name = "fake",
            Provider = "fake",
            ModelId = fake.ModelId,
            PrebuiltProvider = fake
        });
        await host.StartAsync(EchoTool(), r => r.From("direct:agent").To(llmUri).To("mock:done"));
        await host.SendAsync("direct:agent", "go", new Dictionary<string, object?>
        {
            [LlmHeaders.ConversationId] = conversationId
        });
    }

    private static string AgentUri() =>
        LlmDsl.Factory("fake").Tools("echo").ConversationFromHeader().Budget(inputTokens: 100000).AsUri()
        + "&redb=" + NamedRedbFixture.Name;

    [Fact]
    public async Task The_tool_call_audit_lands_in_the_instance_redb_names()
    {
        var conversationId = $"c-{Guid.NewGuid():N}";
        var fake = new FakeProvider().EnqueueToolUse("echo", "{\"v\":1}").EnqueueText("done");
        await using var host = LiveLlmHost.Build(observer: new RedbAuditObserver(_fx.RouteContext));

        await RunAsync(host, fake, AgentUri(), conversationId);

        var named = await _named.Redb.Query<ToolAuditProps>().Where(p => p.ConversationId == conversationId).ToListAsync();
        var business = await _fx.Redb.Query<ToolAuditProps>().Where(p => p.ConversationId == conversationId).ToListAsync();
        named.Should().ContainSingle("the endpoint names the agent's instance");
        business.Should().BeEmpty("the tool's input and output must not reach the business database");
    }

    [Fact]
    public async Task The_conversation_cost_lands_in_the_instance_redb_names()
    {
        var conversationId = $"c-{Guid.NewGuid():N}";
        var fake = new FakeProvider().EnqueueText("done", tokensIn: 7, tokensOut: 3);
        await using var host = LiveLlmHost.Build(
            budget: new StoreBudgetEnforcer(new RedbCostBudgetStore(_fx.RouteContext)));

        await RunAsync(host, fake, AgentUri(), conversationId);

        var named = await new RedbCostBudgetStore(_fx.RouteContext, NamedRedbFixture.Name).GetUsageAsync(conversationId);
        var business = await new RedbCostBudgetStore(_fx.RouteContext).GetUsageAsync(conversationId);
        named.InputTokens.Should().Be(7, "the endpoint names the agent's instance");
        business.InputTokens.Should().Be(0, "nothing of the conversation's cost reaches the business database");
    }

    // ── The registration-level name: AddRedbLlmStorage(name) ──

    private ServiceProvider StorageRegisteredUnder(string redbName)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRouteContext>(_fx.RouteContext);
        services.AddSingleton<IIdempotentRepository>(new InMemoryIdempotentRepository());
        services.AddRedbLlmStorage(redbName);
        return services.BuildServiceProvider();
    }

    private static AgentToolInvocationContext ToolCall(string conversationId, string? redbName) => new()
    {
        Run = new AgentRunContext
        {
            ConversationId = conversationId,
            FactoryName = "test",
            ProviderId = "test",
            ModelId = "test-model",
            ExchangeId = $"x-{Guid.NewGuid():N}",
            RedbName = redbName
        },
        Tool = new LlmToolCapability { Name = "echo", Description = "t", InputSchema = "{}" },
        InputJson = """{"in":1}""",
        OutputJson = """{"ok":true}""",
        ToolUseId = "tu-1",
        Duration = TimeSpan.FromMilliseconds(5)
    };

    [Fact]
    public async Task A_registration_name_is_where_the_audit_and_the_cost_go_without_redb_on_the_uri()
    {
        await using var sp = StorageRegisteredUnder(NamedRedbFixture.Name);
        var conversationId = $"c-{Guid.NewGuid():N}";

        await sp.GetRequiredService<IAgentObserver>().OnToolInvokedAsync(ToolCall(conversationId, redbName: null));
        await sp.GetRequiredService<ICostBudgetStore>().AddAsync(conversationId, new AgentUsage(5, 1, 0m));

        (await _named.Redb.Query<ToolAuditProps>().Where(p => p.ConversationId == conversationId).ToListAsync())
            .Should().ContainSingle();
        (await _fx.Redb.Query<ToolAuditProps>().Where(p => p.ConversationId == conversationId).ToListAsync())
            .Should().BeEmpty();
        (await new RedbCostBudgetStore(_fx.RouteContext, NamedRedbFixture.Name).GetUsageAsync(conversationId))
            .InputTokens.Should().Be(5);
    }

    [Fact]
    public async Task Redb_on_the_uri_wins_over_the_registration_name()
    {
        // The registration names the business instance; the run names the agent's, and the run decides.
        _fx.RouteContext.RegisterRedbService("business", _fx.Redb);
        await using var sp = StorageRegisteredUnder("business");
        var conversationId = $"c-{Guid.NewGuid():N}";

        await sp.GetRequiredService<IAgentObserver>().OnToolInvokedAsync(ToolCall(conversationId, NamedRedbFixture.Name));

        (await _named.Redb.Query<ToolAuditProps>().Where(p => p.ConversationId == conversationId).ToListAsync())
            .Should().ContainSingle();
        (await _fx.Redb.Query<ToolAuditProps>().Where(p => p.ConversationId == conversationId).ToListAsync())
            .Should().BeEmpty();
    }
}
