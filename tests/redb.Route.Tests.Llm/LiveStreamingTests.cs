using System.Net.Http;
using redb.Route.Tests.Llm.TestHelpers;

namespace redb.Route.Tests.Llm;

/// <summary>
/// Live integration tests for provider-level streaming (<see cref="ILlmProvider.StreamAsync"/>).
/// Each test is env-gated and auto-skips when the corresponding key is missing.
/// <para>
/// What we assert (per provider):
/// <list type="bullet">
///   <item>more than one chunk arrives — proves the wire is truly streaming, not a single
///         buffered response delivered in one frame;</item>
///   <item>at least one chunk carries a text delta;</item>
///   <item>the concatenated answer contains the expected substring ("4" for "2+2");</item>
///   <item>some chunk reports a non-null <c>StopReason</c> (the terminal frame).</item>
/// </list>
/// </para>
/// </summary>
[Trait("Category", "LiveLlm")]
[Collection("LiveLlmSerial")]
public sealed class LiveStreamingTests
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(60) };

    private static LlmConnectionFactory MakeFactory(string name, string provider, string modelId, string envVar)
        => new()
        {
            Name = name,
            Provider = provider,
            ModelId = modelId,
            ApiKey = Environment.GetEnvironmentVariable(envVar),
            Temperature = 0.0,
            MaxTokens = 64
        };

    private static LlmRequest MathRequest() => new()
    {
        SystemPrompt = "You are a calculator. Answer with the number only, no words.",
        Messages = [LlmMessage.User("What is 2+2?")],
        Temperature = 0.0,
        MaxTokens = 512
    };

    private sealed record StreamSummary(
        int ChunkCount,
        int TextDeltaCount,
        string Text,
        bool SawStopReason,
        LlmUsage? LastUsage);

    private static async Task<StreamSummary> CollectAsync(ILlmProvider provider, LlmRequest request)
    {
        const int maxAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await CollectOnceAsync(provider, request).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < maxAttempts && Is429(ex.Message))
            {
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt)).ConfigureAwait(false);
            }
        }
    }

    private static async Task<StreamSummary> CollectOnceAsync(ILlmProvider provider, LlmRequest request)
    {
        var chunkCount = 0;
        var textCount = 0;
        var sb = new System.Text.StringBuilder();
        var sawStop = false;
        LlmUsage? lastUsage = null;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await foreach (var chunk in provider.StreamAsync(request, cts.Token).ConfigureAwait(false))
        {
            chunkCount++;
            foreach (var block in chunk.Content)
            {
                if (block is LlmTextBlock text)
                {
                    textCount++;
                    sb.Append(text.Text);
                }
            }
            if (chunk.StopReason is not null) sawStop = true;
            if (chunk.Usage is not null) lastUsage = chunk.Usage;
        }

        return new StreamSummary(chunkCount, textCount, sb.ToString(), sawStop, lastUsage);
    }

    private static bool Is429(string message) =>
        message.Contains("429") || message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase);

    private static async Task SmokeStreamAsync(ILlmProvider provider)
    {
        var summary = await CollectAsync(provider, MathRequest()).ConfigureAwait(false);

        summary.ChunkCount.Should().BeGreaterThan(1,
            $"{provider.ProviderId}: streaming must yield more than one chunk, got {summary.ChunkCount}");
        summary.TextDeltaCount.Should().BeGreaterThan(0,
            $"{provider.ProviderId}: at least one text delta expected");
        summary.Text.Should().Contain("4",
            $"{provider.ProviderId}: accumulated text should contain the answer, got '{summary.Text}'");
        summary.SawStopReason.Should().BeTrue(
            $"{provider.ProviderId}: terminal chunk must carry a StopReason");
    }

    // ---------- Anthropic Claude Haiku (true SSE) ----------
    [EnvFact("REDB_LLM_ANT_API03_KEY")]
    public Task Anthropic_ClaudeHaiku_Stream() => SmokeStreamAsync(
        new AnthropicProvider(
            new LlmConnectionFactory
            {
                Name = "anthropic-haiku",
                Provider = "anthropic",
                ModelId = "claude-haiku-4-5",
                ApiKey = Environment.GetEnvironmentVariable("REDB_LLM_ANT_API03_KEY"),
                Temperature = 0.0,
                MaxTokens = 64
            },
            SharedHttp));

    // ---------- Groq (OpenAI-compatible SSE) ----------
    [EnvFact("REDB_LLM_GROQ_KEY")]
    public Task Groq_Stream() => SmokeStreamAsync(
        new OpenAiProvider(
            MakeFactory("groq", "groq", "llama-3.3-70b-versatile", "REDB_LLM_GROQ_KEY"),
            SharedHttp));

    // ---------- Cerebras ----------
    [EnvFact("REDB_LLM_CEREBRAS_KEY")]
    public Task Cerebras_Stream() => SmokeStreamAsync(
        new OpenAiProvider(
            MakeFactory("cerebras", "cerebras", "gpt-oss-120b", "REDB_LLM_CEREBRAS_KEY"),
            SharedHttp));

    // ---------- Google Gemini (OpenAI-compat) ----------
    [EnvFact("REDB_LLM_GEMINI_KEY")]
    public Task Gemini_Stream() => SmokeStreamAsync(
        new OpenAiProvider(
            MakeFactory("gemini", "gemini", "gemini-2.0-flash", "REDB_LLM_GEMINI_KEY"),
            SharedHttp));

    // ---------- Mistral ----------
    [EnvFact("REDB_LLM_MISTRAL_KEY")]
    public Task Mistral_Stream() => SmokeStreamAsync(
        new OpenAiProvider(
            MakeFactory("mistral", "mistral", "mistral-small-latest", "REDB_LLM_MISTRAL_KEY"),
            SharedHttp));

    // ---------- OpenRouter ----------
    [EnvFact("REDB_LLM_OPENROUTER_KEY")]
    public Task OpenRouter_Stream() => SmokeStreamAsync(
        new OpenAiProvider(
            MakeFactory("openrouter", "openrouter",
                "meta-llama/llama-3.3-70b-instruct:free", "REDB_LLM_OPENROUTER_KEY"),
            SharedHttp));
}
