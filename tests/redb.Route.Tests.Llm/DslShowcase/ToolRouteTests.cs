using redb.Route.Tests.Llm.TestHelpers;
using LlmDsl = redb.Route.Llm.Fluent.Llm;

namespace redb.Route.Tests.Llm.DslShowcase;

/// <summary>
/// The point of <c>redb.Route.Llm</c> is that a tool is just a route. This
/// suite proves it end-to-end against a live provider: an <see cref="EchoToolRoute"/>
/// is mounted with <c>.AsLlmTool</c>, then a chat route asks the model to call
/// it and return the payload. The agent loop runs entirely inside the route
/// engine — no manual JSON shuffling.
/// <para>
/// We only run this against Groq because Groq's tool-use on Llama 3.3 70B is
/// the most reliable free tier we have. Other providers either rate-limit
/// aggressively (Gemini), tool-use is gated behind a paid plan (OpenAI),
/// or the free model refuses tool calls some fraction of the time
/// (Mistral small). Adding more providers here is just an extra
/// <see cref="EnvFactAttribute"/> — the DSL doesn't change.
/// </para>
/// </summary>
[Trait("Category", "LiveLlm")]
[Collection("LiveLlmSerial")]
public sealed class ToolRouteTests
{
    [EnvFact("REDB_LLM_GROQ_KEY")]
    public async Task Groq_ToolLoop_RoutedTool_DeliversPayload()
    {
        await using var host = LiveLlmHost.Build()
            .AddFactory("groq", new LlmConnectionFactory
            {
                Provider = "groq",
                ModelId = "llama-3.3-70b-versatile",
                ApiKey = Environment.GetEnvironmentVariable("REDB_LLM_GROQ_KEY"),
                Temperature = 0.0,
                MaxTokens = 256
            });

        // Tool: takes {"q": string}, returns {"answer": "<q reversed>"}.
        var echo = new EchoToolRoute(
            toolName: "lookup",
            description: "Look up a fact for the given query and return JSON {\"answer\": ...}.",
            inputSchema: """{"type":"object","properties":{"q":{"type":"string"}},"required":["q"]}""",
            replyJson: """{"answer":"the magic word is rosebud"}""");

        await host.StartAsync(echo, r =>
        {
            r.From("direct:agent")
                .Process(e => e.In.Headers[LlmHeaders.SystemPrompt] =
                    "Use the lookup tool to find the magic word, then reply with that word verbatim and nothing else.")
                .To(LlmDsl.Factory("groq").Tools("lookup").MaxIterations(4).AsUri())
                .To("mock:done");
        });

        await host.SendAsync("direct:agent", "What is the magic word?");

        // The agent must have called the tool at least once.
        echo.CapturedInputs.Should().NotBeEmpty();

        var sink = host.Mock("mock:done");
        sink.ReceivedCount.Should().Be(1);
        var reply = ((string)sink.ReceivedExchanges[0].In.Body!).ToLowerInvariant();
        reply.Should().Contain("rosebud");
    }
}
