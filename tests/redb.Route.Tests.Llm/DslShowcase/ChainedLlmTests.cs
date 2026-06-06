using redb.Route.Tests.Llm.TestHelpers;
using LlmDsl = redb.Route.Llm.Fluent.Llm;

namespace redb.Route.Tests.Llm.DslShowcase;

/// <summary>
/// Two LLM hops in a row. First hop summarizes, second hop translates the
/// summary. The route engine carries the body forward; no extra plumbing is
/// needed to chain models. Uses Groq twice with different system prompts —
/// the same factory, parameterized per call site.
/// </summary>
[Trait("Category", "LiveLlm")]
[Collection("LiveLlmSerial")]
public sealed class ChainedLlmTests
{
    [EnvFact("REDB_LLM_GROQ_KEY")]
    public async Task Groq_TwoHopChain_SummarizeThenTranslate()
    {
        await using var host = LiveLlmHost.Build()
            .AddFactory("groq", new LlmConnectionFactory
            {
                Provider = "groq",
                ModelId = "llama-3.3-70b-versatile",
                ApiKey = Environment.GetEnvironmentVariable("REDB_LLM_GROQ_KEY"),
                Temperature = 0.0,
                MaxTokens = 96
            });

        await host.StartAsync(r =>
        {
            r.From("direct:chain")
                // Hop 1 — summarize.
                .Process(e => e.In.Headers[LlmHeaders.SystemPrompt] =
                    "Summarize the user's text in one short English sentence.")
                .To(LlmDsl.Factory("groq").Temperature(0.0).MaxTokens(64).AsUri())
                // Hop 2 — translate the previous reply.
                .Process(e => e.In.Headers[LlmHeaders.SystemPrompt] =
                    "Translate the user's text to French. Reply with the translation only, no preamble.")
                .To(LlmDsl.Factory("groq").Temperature(0.0).MaxTokens(64).AsUri())
                .To("mock:done");
        });

        await host.SendAsync("direct:chain",
            "The quick brown fox jumps over the lazy dog every single morning before breakfast.");

        var sink = host.Mock("mock:done");
        sink.ReceivedCount.Should().Be(1);
        var final = ((string)sink.ReceivedExchanges[0].In.Body!);
        final.Should().NotBeNullOrWhiteSpace();
        // Philosophical: French replies usually contain at least one of these tokens.
        // We don't pin one specific word because models phrase things differently.
        var lower = final.ToLowerInvariant();
        var frenchish = new[] { "le ", "la ", "les ", "un ", "une ", "renard", "chien" };
        frenchish.Any(tok => lower.Contains(tok)).Should().BeTrue(
            "the second hop should yield French-shaped output, but got: {0}", final);
    }
}
