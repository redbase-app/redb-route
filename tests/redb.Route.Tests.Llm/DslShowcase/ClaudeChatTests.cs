using redb.Route.Tests.Llm.TestHelpers;
using LlmDsl = redb.Route.Llm.Fluent.Llm;

namespace redb.Route.Tests.Llm.DslShowcase;

/// <summary>
/// Live tests against Anthropic's Claude family through the OpenAI-compatible
/// endpoint at <c>https://api.anthropic.com/v1/</c>. Same DSL shapes as the rest
/// of <c>DslShowcase</c> — a different vendor key, a different model, no other
/// changes to the route definitions. The one extension we made to support this
/// is wiring <c>"anthropic"</c> / <c>"claude"</c> into <c>OpenAiProvider.ResolveDefaultBaseUrl</c>.
/// <para>
/// Claude tends to be the most reliable closed-source model we have access to,
/// so this is the strict-assertion suite — the model must produce literal
/// tokens we ask for.
/// </para>
/// </summary>
[Trait("Category", "LiveLlm")]
[Collection("LiveLlmSerial")]
public sealed class ClaudeChatTests
{
    [EnvFact("REDB_LLM_ANT_API03_KEY")]
    public async Task Claude_Haiku_45_From_To_Mock_DeliversAnswer()
    {
        await using var host = LiveLlmHost.Build()
            .AddFactory("claude", new LlmConnectionFactory
            {
                Provider = "anthropic",
                ModelId = "claude-haiku-4-5",
                ApiKey = Environment.GetEnvironmentVariable("REDB_LLM_ANT_API03_KEY"),
                Temperature = 0.0,
                MaxTokens = 32
            });

        await host.StartAsync(r =>
        {
            r.From("direct:chat")
                .Process(e => e.In.Headers[LlmHeaders.SystemPrompt] =
                    "Reply with the literal token 'pong' and nothing else. No punctuation.")
                .To(LlmDsl.Factory("claude").Temperature(0.0).MaxTokens(8).AsUri())
                .To("mock:result");
        });

        await host.SendAsync("direct:chat", "ping");

        var sink = host.Mock("mock:result");
        sink.ReceivedCount.Should().Be(1);
        var reply = ((string)sink.ReceivedExchanges[0].In.Body!).Trim().ToLowerInvariant();
        reply.Should().Contain("pong",
            "Haiku 4.5 reliably follows 'reply with the literal token X' instructions");
    }

    [EnvFact("REDB_LLM_ANT_API03_KEY")]
    public async Task Claude_Haiku_45_Tools_LookupAgent_UsesTool()
    {
        var lookup = new EchoToolRoute(
            toolName: "lookup",
            description: "Look up the magic word for a key.",
            inputSchema: """{"type":"object","properties":{"key":{"type":"string"}},"required":["key"]}""",
            replyJson: """{"answer":"the magic word is rosebud"}""");

        await using var host = LiveLlmHost.Build()
            .AddFactory("claude", new LlmConnectionFactory
            {
                Provider = "anthropic",
                ModelId = "claude-haiku-4-5",
                ApiKey = Environment.GetEnvironmentVariable("REDB_LLM_ANT_API03_KEY"),
                Temperature = 0.0,
                MaxTokens = 128
            });

        await host.StartAsync(lookup, r =>
        {
            r.From("direct:agent")
                .Process(e => e.In.Headers[LlmHeaders.SystemPrompt] =
                    "Use the lookup tool to find the magic word, then reply with that exact phrase. " +
                    "Do not invent a word — call the tool first.")
                .To(LlmDsl.Factory("claude").Tools("lookup").MaxIterations(4).AsUri())
                .To("mock:done");
        });

        await host.SendAsync("direct:agent", "What is the magic word?");

        lookup.CapturedInputs.Should().NotBeEmpty("Claude must call the lookup tool at least once");

        var sink = host.Mock("mock:done");
        sink.ReceivedCount.Should().Be(1);
        var final = ((string)sink.ReceivedExchanges[0].In.Body!).ToLowerInvariant();
        final.Should().Contain("rosebud", "the final reply must echo the tool result");
    }

    /// <summary>
    /// A larger model on a slightly fuzzier task — translate then summarize via two
    /// LLM hops in one route. Loose assertion (French-shaped output) — Sonnet has
    /// more degrees of freedom than Haiku and we don't want to pin specific tokens.
    /// </summary>
    [EnvFact("REDB_LLM_ANT_API03_KEY")]
    public async Task Claude_Sonnet_46_TwoHopChain_SummarizeThenTranslate()
    {
        await using var host = LiveLlmHost.Build()
            .AddFactory("claude", new LlmConnectionFactory
            {
                Provider = "anthropic",
                ModelId = "claude-sonnet-4-6",
                ApiKey = Environment.GetEnvironmentVariable("REDB_LLM_ANT_API03_KEY"),
                Temperature = 0.0,
                MaxTokens = 96
            });

        await host.StartAsync(r =>
        {
            r.From("direct:chain")
                .Process(e => e.In.Headers[LlmHeaders.SystemPrompt] =
                    "Summarize the user's text in one short English sentence.")
                .To(LlmDsl.Factory("claude").Temperature(0.0).MaxTokens(64).AsUri())
                .Process(e => e.In.Headers[LlmHeaders.SystemPrompt] =
                    "Translate the user's text to French. Reply with the translation only, no preamble.")
                .To(LlmDsl.Factory("claude").Temperature(0.0).MaxTokens(64).AsUri())
                .To("mock:done");
        });

        await host.SendAsync("direct:chain",
            "The quick brown fox jumps over the lazy dog every single morning before breakfast.");

        var sink = host.Mock("mock:done");
        sink.ReceivedCount.Should().Be(1);
        var final = ((string)sink.ReceivedExchanges[0].In.Body!);
        final.Should().NotBeNullOrWhiteSpace();
        var lower = final.ToLowerInvariant();
        var frenchish = new[] { "le ", "la ", "les ", "un ", "une ", "renard", "chien" };
        frenchish.Any(tok => lower.Contains(tok)).Should().BeTrue(
            "the second hop should yield French-shaped output, but got: {0}", final);
    }
}
