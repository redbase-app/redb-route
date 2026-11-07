using redb.Route.Tests.Llm.TestHelpers;
using LlmDsl = redb.Route.Llm.Fluent.Llm;

namespace redb.Route.Tests.Llm.DslShowcase;

/// <summary>
/// The simplest live shape — an Apache Camel pattern reduced to one line:
/// <code>From("direct:chat") → To("llm://...") → To("mock:result")</code>.
/// One <c>direct:</c> producer, one LLM hop, one assertion sink. Any junior
/// reader should grasp the shape on first sight.
/// <para>
/// Each test gates on the env var of the corresponding free-tier provider.
/// We don't pretend the cheapest free tiers are equally reliable — the suite
/// shows the connector <i>works in principle</i> across providers, not that
/// every provider is production-grade.
/// </para>
/// </summary>
[Trait("Category", "LiveLlm")]
[Collection("LiveLlmSerial")]
public sealed class BasicChatTests
{
    /// <summary>
    /// Groq + Llama 3.3 70B is the most reliable free tier we have access to,
    /// so it gets the strict assertion (the model must produce the literal word).
    /// </summary>
    [EnvFact("REDB_LLM_GROQ_KEY")]
    public async Task Groq_From_To_Mock_DeliversAnswer()
    {
        await using var host = LiveLlmHost.Build()
            .AddFactory("groq", new LlmConnectionFactory
            {
                Provider = "groq",
                ModelId = "llama-3.3-70b-versatile",
                ApiKey = Environment.GetEnvironmentVariable("REDB_LLM_GROQ_KEY"),
                Temperature = 0.0,
                MaxTokens = 16
            });

        await host.StartAsync(r =>
        {
            r.From("direct:chat")
                .Process(e => e.In.Headers[LlmHeaders.SystemPrompt] =
                    "Reply with exactly one lowercase word: pong. No punctuation.")
                .To(LlmDsl.Factory("groq").Temperature(0.0).MaxTokens(16).AsUri())
                .To("mock:result");
        });

        await host.SendAsync("direct:chat", "Say the word.");

        var sink = host.Mock("mock:result");
        sink.ReceivedCount.Should().Be(1);
        var reply = ((string)sink.ReceivedExchanges[0].In.Body!).Trim().ToLowerInvariant();
        reply.Should().Contain("pong");

        // Headers stamped by LlmProducer must propagate down the route.
        sink.ReceivedExchanges[0].In.Headers[LlmHeaders.ProviderId].Should().Be("groq");
        ((int)sink.ReceivedExchanges[0].In.Headers[LlmHeaders.TokensIn]!).Should().BeGreaterThan(0);
    }

    /// <summary>
    /// Gemini's free tier is rate-limited to 15 RPM; on a quiet repo it works,
    /// but we keep the assertion philosophical: <i>some non-empty reply</i>
    /// rather than a specific token. Demonstrates that switching provider is
    /// just a registry entry change — no other DSL surface moves.
    /// </summary>
    [EnvFact("REDB_LLM_GEMINI_KEY")]
    public async Task Gemini_From_To_Mock_DeliversNonEmptyReply()
    {
        await using var host = LiveLlmHost.Build()
            .AddFactory("gemini", new LlmConnectionFactory
            {
                Provider = "gemini",
                ModelId = "gemini-2.0-flash",
                ApiKey = Environment.GetEnvironmentVariable("REDB_LLM_GEMINI_KEY"),
                Temperature = 0.0,
                MaxTokens = 32
            });

        await host.StartAsync(r =>
        {
            r.From("direct:chat")
                .Process(e => e.In.Headers[LlmHeaders.SystemPrompt] =
                    "Reply with one short sentence in plain English.")
                .To(LlmDsl.Factory("gemini").AsUri())
                .To("mock:result");
        });

        await host.SendAsync("direct:chat", "Greet me.");

        var sink = host.Mock("mock:result");
        sink.ReceivedCount.Should().Be(1);
        ((string)sink.ReceivedExchanges[0].In.Body!).Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Mistral's small-latest is the second-most reliable free tier for
    /// short-form replies. Same DSL — only the registry name changes.
    /// </summary>
    [EnvFact("REDB_LLM_MISTRAL_KEY")]
    public async Task Mistral_From_To_Mock_DeliversNonEmptyReply()
    {
        await using var host = LiveLlmHost.Build()
            .AddFactory("mistral", new LlmConnectionFactory
            {
                Provider = "mistral",
                ModelId = "mistral-small-latest",
                ApiKey = Environment.GetEnvironmentVariable("REDB_LLM_MISTRAL_KEY"),
                Temperature = 0.0,
                MaxTokens = 32
            });

        await host.StartAsync(r =>
        {
            r.From("direct:chat")
                .Process(e => e.In.Headers[LlmHeaders.SystemPrompt] =
                    "Reply with one short sentence in plain English.")
                .To(LlmDsl.Factory("mistral").AsUri())
                .To("mock:result");
        });

        await host.SendAsync("direct:chat", "Greet me.");

        var sink = host.Mock("mock:result");
        sink.ReceivedCount.Should().Be(1);
        ((string)sink.ReceivedExchanges[0].In.Body!).Should().NotBeNullOrWhiteSpace();
    }
}
