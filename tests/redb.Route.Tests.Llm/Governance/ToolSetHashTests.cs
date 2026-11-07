using redb.Route.Llm.Engine.Storage;
using redb.Route.Tests.Llm.TestHelpers;
using LlmDsl = redb.Route.Llm.Fluent.Llm;

namespace redb.Route.Tests.Llm.Governance;

/// <summary>
/// Wave 0 — red before the fix. <c>MessageProps.ToolSetHash</c> exists so an auditor can answer
/// "did the same prompt see a different tool surface?"; today the hash is computed from
/// name + description + input schema only, so a changed <c>LlmToolSafety</c> (a weaker gate)
/// leaves the hash untouched and the drift is invisible.
/// <para>
/// The test runs the same tool twice with different side-effect declarations and compares the
/// persisted hash — the observable auditors work with.
/// </para>
/// </summary>
[Trait("Category", "Governance")]
public sealed class ToolSetHashTests
{
    private const string Schema = """{"type":"object","properties":{"q":{"type":"string"}},"required":["q"]}""";
    private const string ConversationId = "c-hash";

    [Fact]
    public async Task ToolSetHash_DiffersWhenSafetyDiffers()
    {
        var readOnlyHash = await RunAndReadHashAsync(ToolSideEffect.ReadOnly);
        var mutatingHash = await RunAndReadHashAsync(ToolSideEffect.Mutating);

        readOnlyHash.Should().NotBeNull("the assistant message persists the tool surface hash");
        mutatingHash.Should().NotBeNull();
        mutatingHash.Should().NotBe(readOnlyHash,
            "a policy change is part of the tool surface an auditor compares between runs");
    }

    [Fact]
    public async Task ToolSetHash_DistinguishesClaimSplitting()
    {
        var singleClaim = await RunAndReadHashAsync(ToolSideEffect.ReadOnly, "a,b");
        var twoClaims = await RunAndReadHashAsync(ToolSideEffect.ReadOnly, "a", "b");

        singleClaim.Should().NotBeNull();
        twoClaims.Should().NotBe(singleClaim,
            "one claim named \"a,b\" and two claims \"a\",\"b\" are different requirements — a comma-joined hash would confuse them");
    }

    private static async Task<string?> RunAndReadHashAsync(ToolSideEffect sideEffect, params string[] claims)
    {
        var provider = new FakeProvider()
            .EnqueueToolUse("lookup", """{"q":"x"}""", "tu_1")
            .EnqueueText("done");

        var store = new InMemoryConversationStore();
        var tool = new EchoToolRoute("lookup", "Look up a fact.", Schema, """{"answer":"42"}""",
            sideEffect: sideEffect, requiredClaims: claims);

        // A claims source is required for a claim-declaring tool to build at all; the hash is computed
        // from the declaration, so the run may still be denied the call.
        await using var host = LiveLlmHost.Build(
                conversation: store,
                claimsSource: new DelegateToolClaimsSource(_ => null))
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

        await host.StartAsync(tool, r => r.From("direct:agent")
            .To(LlmDsl.Factory("demo").ConversationFromHeader().Tools("lookup").MaxIterations(4).AsUri())
            .To("mock:done"));

        await host.SendAsync("direct:agent", "Call the tool.",
            new Dictionary<string, object?> { [LlmHeaders.ConversationId] = ConversationId });

        var path = await store.LoadPathAsync(ConversationId);
        return path.Select(m => m.Meta.ToolSetHash).FirstOrDefault(hash => hash is not null);
    }
}
