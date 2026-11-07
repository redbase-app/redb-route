using redb.Route.Tests.Llm.TestHelpers;
using LlmDsl = redb.Route.Llm.Fluent.Llm;

namespace redb.Route.Tests.Llm.Governance;

/// <summary>
/// Idempotency gate (wave 5). The spec gates the idempotency store on the declared side-effect class:
/// a mutating tool is reserved before it runs and a replay of the same <c>tool_use</c> id returns the
/// stored result, while a read-only tool is never reserved — it has no side effect to de-duplicate and
/// a reservation would cost store round-trips on every call.
/// <para>
/// Both tests set a conversation id (via <c>?conversation=header</c>); without one the engine's
/// idempotency branch has nowhere to store anything.
/// </para>
/// </summary>
[Trait("Category", "Governance")]
public sealed class IdempotencyGateTests
{
    private const string Schema = """{"type":"object","properties":{"q":{"type":"string"}},"required":["q"]}""";
    private const string ConversationId = "c-1";
    private const string Input = """{"q":"x"}""";
    private const string Reply = """{"answer":"42"}""";

    [Fact]
    public async Task ReadOnlyTool_DoesNotConsultIdempotencyStore()
    {
        var provider = new FakeProvider()
            .EnqueueToolUse("lookup", """{"q":"x"}""", "tu_1")
            .EnqueueText("done");
        var idempotency = new SpyIdempotencyStore();

        var tool = new EchoToolRoute("lookup", "Look up a fact.", Schema, """{"answer":"42"}""",
            sideEffect: ToolSideEffect.ReadOnly);

        await using var host = LiveLlmHost.Build(idempotency: idempotency)
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

        await host.StartAsync(tool, r => r.From("direct:agent")
            .To(LlmDsl.Factory("demo").ConversationFromHeader().Tools("lookup").MaxIterations(4).AsUri())
            .To("mock:done"));

        await host.SendAsync("direct:agent", "Call the tool.",
            new Dictionary<string, object?> { [LlmHeaders.ConversationId] = ConversationId });

        tool.CapturedInputs.Should().HaveCount(1, "the tool itself still runs");
        idempotency.Reservations.Should().BeEmpty(
            "a read-only tool has no side effect to deduplicate — reserving it costs a store round-trip for nothing");
    }

    [Fact]
    public async Task MutatingTool_ReservesAndCompletesOnce()
    {
        var provider = new FakeProvider()
            .EnqueueToolUse("write", """{"q":"x"}""", "tu_1")
            .EnqueueText("done");
        var idempotency = new SpyIdempotencyStore();

        var tool = new EchoToolRoute("write", "Mutate something.", Schema, """{"answer":"ok"}""",
            sideEffect: ToolSideEffect.Mutating);

        await using var host = LiveLlmHost.Build(idempotency: idempotency)
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

        await host.StartAsync(tool, r => r.From("direct:agent")
            .To(LlmDsl.Factory("demo").ConversationFromHeader().Tools("write").MaxIterations(4).AsUri())
            .To("mock:done"));

        await host.SendAsync("direct:agent", "Mutate.",
            new Dictionary<string, object?> { [LlmHeaders.ConversationId] = ConversationId });

        tool.CapturedInputs.Should().HaveCount(1);
        idempotency.Reservations.Should().HaveCount(1, "a mutating tool is reserved before it runs");
        idempotency.Completions.Should().HaveCount(1, "and completed after it succeeded");
    }

    [Fact]
    public async Task MutatingTool_Replay_IsServedFromStoreAndReportedAsIdempotencyHit()
    {
        var provider = new FakeProvider()
            .EnqueueToolUse("write", Input, "tu_1")
            .EnqueueText("done")
            .EnqueueToolUse("write", Input, "tu_1")
            .EnqueueText("done");
        var idempotency = new SpyIdempotencyStore();
        var spy = new ToolInvocationSpy();

        var tool = new EchoToolRoute("write", "Mutate something.", Schema, Reply,
            sideEffect: ToolSideEffect.Mutating);

        await using var host = LiveLlmHost.Build(spy, idempotency: idempotency)
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

        await host.StartAsync(tool, r => r.From("direct:agent")
            .To(LlmDsl.Factory("demo").ConversationFromHeader().Tools("write").MaxIterations(4).AsUri())
            .To("mock:done"));

        var headers = new Dictionary<string, object?> { [LlmHeaders.ConversationId] = ConversationId };
        await host.SendAsync("direct:agent", "Mutate.", headers);
        await host.SendAsync("direct:agent", "Mutate.", headers);

        tool.CapturedInputs.Should().HaveCount(1,
            "the replayed tool_use id is answered from the store instead of mutating a second time");
        idempotency.Reservations.Should().HaveCount(2,
            "the engine still asks the store on the replay — that question is the protection");
        spy.ForTool("write").Should().Contain(i => i.Skipped && i.SkipReason == ToolSkipReasons.IdempotencyHit,
            "the replay is reported to observers under its own reason, not as a cache hit");
    }
}
