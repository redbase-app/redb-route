using System.Security.Claims;
using redb.Route.Abstractions;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Tests.Llm.TestHelpers;
using LlmDsl = redb.Route.Llm.Fluent.Llm;

namespace redb.Route.Tests.Llm.DslShowcase;

/// <summary>
/// Live governance: a real model, a real tool loop, a real claim check — the level every other live test
/// skips, because they prove the transport and this proves the policy. The key is DeepSeek's (the one
/// provider whose key is valid in this environment); the behaviour under test is provider-independent.
/// <para>
/// The caller's scope is put on the exchange the way a transport would (a property, see
/// <see cref="ExchangePrincipal"/>), and the tool declares what it needs — so the two runs below differ in
/// exactly one thing: whether the principal holds that scope.
/// </para>
/// </summary>
[Trait("Category", "LiveLlm")]
[Collection("LiveLlmSerial")]
public sealed class DeepSeekGovernanceLiveTests
{
    private const string Schema =
        """{"type":"object","properties":{"orderId":{"type":"string"}},"required":["orderId"]}""";

    private const string ToolName = "order_lookup";
    private const string Claim = "orders:read";

    private static LlmConnectionFactory DeepSeek() => new()
    {
        Provider = "deepseek",
        ModelId = "deepseek-flash",
        ApiKey = Environment.GetEnvironmentVariable("REDB_LLM_DEEPSEEK"),
        Temperature = 0.0,
        MaxTokens = 256
    };

    private static ClaimsPrincipal Caller(string scope) =>
        new(new ClaimsIdentity([new Claim("scope", scope)], authenticationType: "live-test"));

    private static async Task<(ToolInvocationSpy Spy, EchoToolRoute Tool, LiveLlmHost Host)> RunAsync(string grantedScope)
    {
        var spy = new ToolInvocationSpy();
        var tool = new EchoToolRoute(ToolName, "Look up an order by id and return its status.",
            Schema, """{"status":"shipped"}""", requiredClaims: [Claim]);

        var host = await LiveLlmHost.Build(spy, claimsSource: new ExchangePrincipalClaimsSource())
            .AddFactory("deepseek", DeepSeek())
            .StartAsync(tool, r => r.From("direct:agent")
                .Process(e => e.In.Headers[LlmHeaders.SystemPrompt] =
                    $"Use the {ToolName} tool to look up the order, then reply with only the status word.")
                .To(LlmDsl.Factory("deepseek").Tools(ToolName).MaxIterations(4).AsUri())
                .To("mock:done"));

        await host.SendAsync("direct:agent", "What is the status of order 42?",
            configure: ex => ExchangePrincipal.Set(ex, Caller(grantedScope)));

        return (spy, tool, host);
    }

    [EnvFact("REDB_LLM_DEEPSEEK")]
    public async Task DeepSeek_ClaimRequiredTool_RunsForACallerHoldingTheScope()
    {
        var (spy, tool, host) = await RunAsync(Claim);
        await using var _ = host;

        spy.ForTool(ToolName).Should().NotBeEmpty(
            "the model must call the tool, and the caller's scope satisfies its requirement");
        spy.ForTool(ToolName).Should().OnlyContain(o => !o.Skipped);
        tool.CapturedInputs.Should().HaveCount(1, "the tool route actually ran");
        tool.CapturedInputs[0].Should().Contain("42").And.Contain("orderId");

        var sink = host.Mock("mock:done");
        sink.ReceivedCount.Should().Be(1);
        ((string)sink.ReceivedExchanges[0].In.Body!).Should().Contain("shipped",
            "the answer the model produced carries the tool's output");
    }

    [EnvFact("REDB_LLM_DEEPSEEK")]
    public async Task DeepSeek_ClaimRequiredTool_IsDeniedForACallerWithoutTheScope()
    {
        // The caller is authenticated and holds a different scope. The model still asks for the tool — and
        // the engine refuses the call, so the tool route never runs; what the observer sees is a skip with a
        // machine-readable reason, not an invocation.
        var (spy, tool, host) = await RunAsync("billing:read");
        await using var _ = host;

        spy.ForTool(ToolName).Should().NotBeEmpty("the model asked for the tool — a refusal is not a silent drop");
        spy.ForTool(ToolName).Should().OnlyContain(o => o.Skipped);
        spy.ForTool(ToolName).Last().SkipReason.Should().StartWith(ToolSkipReasons.ClaimsMissing,
            "the refusal carries the reason an audit dashboard filters on");

        tool.CapturedInputs.Should().BeEmpty("refused means the tool route never executed");
        host.Mock("mock:done").ReceivedCount.Should().Be(1,
            "the run finishes and the model answers — a refusal is a tool_result, not a crash");
    }
}
