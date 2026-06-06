using redb.Route.Tests.Llm.TestHelpers;

namespace redb.Route.Tests.Llm.DslShowcase;

/// <summary>
/// Shows the second flavor of LLM step: an inline <c>.Llm(name, b => ...)</c>
/// fluent call instead of a <c>llm://</c> URI. Functionally equivalent to
/// <see cref="BasicChatTests"/> but reads more naturally when the route has a
/// strong opinion about parameters (system prompt, max iterations, etc.).
/// <para>
/// We keep one strict assertion (Groq must produce the literal token) and one
/// philosophical assertion (Mistral must produce <i>some</i> reply), mirroring
/// the spectrum of free-tier reliability we see in practice.
/// </para>
/// </summary>
[Trait("Category", "LiveLlm")]
[Collection("LiveLlmSerial")]
public sealed class InlineLlmTests
{
    [EnvFact("REDB_LLM_GROQ_KEY")]
    public async Task Groq_InlineLlm_Step_DeliversAnswer()
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
                .Llm("groq", b => b
                    .WithSystemPrompt("Reply with exactly one lowercase word: pong. No punctuation.")
                    .WithTemperature(0.0)
                    .WithMaxTokens(16))
                .To("mock:result");
        });

        await host.SendAsync("direct:chat", "Say the word.");

        var sink = host.Mock("mock:result");
        sink.ReceivedCount.Should().Be(1);
        var reply = ((string)sink.ReceivedExchanges[0].In.Body!).Trim().ToLowerInvariant();
        reply.Should().Contain("pong");
    }

    [EnvFact("REDB_LLM_MISTRAL_KEY")]
    public async Task Mistral_InlineLlm_Step_DeliversNonEmptyReply()
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
                .Llm("mistral", b => b
                    .WithSystemPrompt("Reply with one short sentence in plain English.")
                    .WithMaxTokens(32))
                .To("mock:result");
        });

        await host.SendAsync("direct:chat", "Greet me.");

        var sink = host.Mock("mock:result");
        sink.ReceivedCount.Should().Be(1);
        ((string)sink.ReceivedExchanges[0].In.Body!).Should().NotBeNullOrWhiteSpace();
    }
}
