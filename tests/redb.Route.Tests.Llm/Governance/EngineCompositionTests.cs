using System.Security.Claims;
using redb.Route.Tests.Llm.TestHelpers;
using LlmDsl = redb.Route.Llm.Fluent.Llm;

namespace redb.Route.Tests.Llm.Governance;

/// <summary>
/// The composition path, which is not the tests' path: every other test here hand-wires the engine
/// (<c>new AgentEngine(...)</c>), while a real host lets <c>AddRedbRouteLlm()</c> build it. If that path
/// loses a governance seam, every promise of this phase holds only inside the test suite — so the
/// container's engine is exercised end-to-end here.
/// </summary>
[Trait("Category", "Governance")]
public sealed class EngineCompositionTests
{
    private const string Schema = """{"type":"object","properties":{"q":{"type":"string"}},"required":["q"]}""";
    private const string Input = """{"q":"x"}""";
    private const string Claim = "orders:read";

    /// <summary>
    /// A claims-required tool through the container's engine: the tool fires only if the engine the
    /// container built carries the registered claims source and a producer template.
    /// </summary>
    [Fact]
    public async Task EngineFromDi_DispatchesAClaimRequiredTool()
    {
        var provider = new FakeProvider()
            .EnqueueToolUse("lookup", Input, "tu_1")
            .EnqueueText("done");

        var tool = new EchoToolRoute("lookup", "Look up a fact.", Schema, """{"answer":"42"}""",
            requiredClaims: [Claim]);

        await using var host = LiveLlmHost.Build(engineFromDi: true)
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

        await host.StartAsync(tool, r => r.From("direct:agent")
            .To(LlmDsl.Factory("demo").Tools("lookup").MaxIterations(4).AsUri())
            .To("mock:done"));

        var principal = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("scope", Claim)], authenticationType: "test"));

        await host.SendAsync("direct:agent", "Call the tool.",
            configure: ex => ExchangePrincipal.Set(ex, principal));

        var sinkBody = host.Mock("mock:done").ReceivedExchanges[0].In.Body?.ToString() ?? "(no body)";
        tool.CapturedInputs.Should().HaveCount(1,
            $"AddRedbRouteLlm() registers the default claims source — the engine it builds must be the one using it. Sink said: {sinkBody}");
    }

    /// <summary>
    /// A read-only, Persist-cached tool through the container's engine: the second identical call inside
    /// one run is answered from the cache, which only happens if the container wired the cache layer into
    /// the engine it built.
    /// </summary>
    [Fact]
    public async Task EngineFromDi_AnswersARepeatedCallFromTheCache()
    {
        var provider = new FakeProvider()
            .EnqueueToolUse("lookup", Input, "tu_1")
            .EnqueueToolUse("lookup", Input, "tu_2")
            .EnqueueText("done");

        var tool = new EchoToolRoute("lookup", "Look up a fact.", Schema, """{"answer":"42"}""",
            caching: ToolCachingPolicy.Memoize);

        await using var host = LiveLlmHost.Build(engineFromDi: true)
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

        await host.StartAsync(tool, r => r.From("direct:agent")
            .To(LlmDsl.Factory("demo").Tools("lookup").MaxIterations(4).AsUri())
            .To("mock:done"));

        await host.SendAsync("direct:agent", "Call the tool twice.");

        tool.CapturedInputs.Should().HaveCount(1,
            "the container's engine must consult the cache it registered, not only the hand-wired one");
    }
}