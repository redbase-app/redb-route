using redb.Route.Llm.Engine.Storage;
using redb.Route.Tests.Llm.TestHelpers;

namespace redb.Route.Tests.Llm;

/// <summary>
/// Producer-level coverage for the audit-extension wiring (3.1.1):
/// <c>?user=</c> + <c>?audit=</c> URI options + <c>llm.user.id</c> /
/// <c>llm.audit.&lt;name&gt;</c> headers reach the conversation store
/// stamped on every persisted row of the run. Backed by
/// <see cref="InMemoryConversationStore"/> — no DB required.
/// </summary>
public sealed class LlmAuditPersistenceTests
{
    private static (RouteContext ctx, LlmEndpoint endpoint, InMemoryConversationStore store)
        Build(FakeProvider provider, string uri)
    {
        var ctx = new RouteContext();
        var component = new LlmComponent();
        ctx.AddComponent(component);

        ctx.AddToRegistry("fake", new LlmConnectionFactory
        {
            Name = "fake",
            Provider = "fake",
            ModelId = provider.ModelId,
            PrebuiltProvider = provider
        });

        var store = new InMemoryConversationStore();
        ctx.AddService(typeof(IAgentEngine), new AgentEngine(
            logger: null,
            producerTemplate: null,
            observer: null,
            budget: null,
            approval: null,
            redaction: null,
            shadow: null,
            conversation: store,
            idempotency: null,
            approvalStore: null));

        var endpoint = (LlmEndpoint)component.CreateEndpoint(EndpointUriParser.Parse(uri));
        return (ctx, endpoint, store);
    }

    private static async Task<IReadOnlyList<ConversationMessage>> RunAsync(string uri, Action<Message>? configureIn = null)
    {
        var fake = new FakeProvider().EnqueueText("ok", tokensIn: 1, tokensOut: 1);
        var (_, endpoint, store) = Build(fake, uri);
        var producer = (LlmProducer)endpoint.CreateProducer();
        await producer.Start();

        var msg = new Message("hi");
        msg.Headers[LlmHeaders.ConversationId] = "c-test";
        configureIn?.Invoke(msg);
        await producer.Process(new Exchange(msg));

        return await store.LoadTreeAsync("c-test");
    }

    [Fact]
    public async Task User_LiteralOption_StampedOnEveryRow()
    {
        var rows = await RunAsync("llm://fake?user=system&conversation=header");

        rows.Should().NotBeEmpty();
        rows.Select(r => r.Meta.UserId).Should().AllBeEquivalentTo("system");
    }

    [Fact]
    public async Task User_HeaderExpression_ResolvesAtRunTime()
    {
        var encoded = "%24%7Bheader.X-User-Id%7D"; // ${header.X-User-Id}
        var rows = await RunAsync(
            $"llm://fake?user={encoded}&conversation=header",
            m => m.Headers["X-User-Id"] = "alice@example.com");

        rows.Select(r => r.Meta.UserId).Should().AllBeEquivalentTo("alice@example.com");
    }

    [Fact]
    public async Task User_HeaderFallback_FromLlmUserIdHeader()
    {
        var rows = await RunAsync(
            "llm://fake?conversation=header",
            m => m.Headers[LlmHeaders.UserId] = "bob");

        rows.Select(r => r.Meta.UserId).Should().AllBeEquivalentTo("bob");
    }

    [Fact]
    public async Task Audit_OptionCsv_IsStampedOnEveryRow()
    {
        var rows = await RunAsync(
            "llm://fake?conversation=header&audit=tier%3Dgold%2Cbucket%3DA");

        rows.Should().NotBeEmpty();
        foreach (var r in rows)
        {
            r.Meta.AuditTags.Should().NotBeNull();
            r.Meta.AuditTags!["tier"].Should().Be("gold");
            r.Meta.AuditTags!["bucket"].Should().Be("A");
        }
    }

    [Fact]
    public async Task Audit_HeaderTags_AreMergedAndWinOnCollision()
    {
        var rows = await RunAsync(
            "llm://fake?conversation=header&audit=tier%3Dgold%2Cbucket%3DA",
            m =>
            {
                m.Headers["llm.audit.tier"] = "platinum";   // overrides option-side "gold"
                m.Headers["llm.audit.region"] = "eu-west";  // adds new key
            });

        var tags = rows[0].Meta.AuditTags!;
        tags["tier"].Should().Be("platinum");
        tags["bucket"].Should().Be("A");
        tags["region"].Should().Be("eu-west");
    }

    [Fact]
    public async Task PromptTemplate_NameAndVersion_AreStampedOnEveryRow()
    {
        var rows = await RunAsync(
            "llm://fake?conversation=header&promptTemplateName=triage&promptTemplateVersion=v3");

        rows.Should().NotBeEmpty();
        rows.Select(r => r.Meta.PromptTemplateName).Should().AllBeEquivalentTo("triage");
        rows.Select(r => r.Meta.PromptTemplateVersion).Should().AllBeEquivalentTo("v3");
    }

    [Fact]
    public async Task User_OptionWins_OverHeaderFallback()
    {
        var rows = await RunAsync(
            "llm://fake?conversation=header&user=system",
            m => m.Headers[LlmHeaders.UserId] = "ignored");

        rows.Select(r => r.Meta.UserId).Should().AllBeEquivalentTo("system");
    }

    [Fact]
    public async Task NoUserNoAudit_LeavesMetaNull()
    {
        var rows = await RunAsync("llm://fake?conversation=header");

        rows.Should().NotBeEmpty();
        rows.Should().OnlyContain(r => r.Meta.UserId == null);
        rows.Should().OnlyContain(r => r.Meta.AuditTags == null);
    }
}
