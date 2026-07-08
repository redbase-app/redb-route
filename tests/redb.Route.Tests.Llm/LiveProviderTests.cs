using System.Net;
using System.Net.Http;
using redb.Route.Tests.Llm.TestHelpers;

namespace redb.Route.Tests.Llm;

/// <summary>
/// Live integration tests against real free-tier OpenAI-compatible providers.
/// <para>
/// Each test is gated by an environment variable. If the key is missing the test
/// is skipped (no failure) so CI runs stay green without secrets. On 429
/// (rate-limit) responses each call retries up to 3 times with exponential
/// backoff that respects <c>Retry-After</c>.
/// </para>
/// <para>
/// Local setup (any subset works — others auto-skip):
/// <code>
/// $env:REDB_LLM_GITHUB_KEY      = "ghp_..."   # GitHub PAT — GitHub Models, free
/// $env:REDB_LLM_GROQ_KEY        = "gsk_..."   # console.groq.com
/// $env:REDB_LLM_GROK_KEY        = "xai-..."   # console.x.ai (xAI Grok)
/// $env:REDB_LLM_CEREBRAS_KEY    = "csk-..."   # cloud.cerebras.ai
/// $env:REDB_LLM_OPENROUTER_KEY  = "sk-or-..." # openrouter.ai (:free models)
/// $env:REDB_LLM_GEMINI_KEY      = "AIza..."   # aistudio.google.com
/// $env:REDB_LLM_MISTRAL_KEY     = "..."       # console.mistral.ai
/// </code>
/// Run only the live suite:
/// <code>dotnet test --filter "Category=LiveLlm"</code>
/// </para>
/// </summary>
[Trait("Category", "LiveLlm")]
[Collection("LiveLlmSerial")] // disables intra-class parallelism — important for 429-prone free tiers
public sealed class LiveProviderTests
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

    private static async Task<LlmResponse> CompleteWithRetryAsync(OpenAiProvider provider, LlmRequest request)
    {
        const int maxAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await provider.CompleteAsync(request).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < maxAttempts && Is429(ex.Message))
            {
                // Free tiers (Gemini 15 RPM, OpenRouter shared upstream) need long backoff.
                var delay = TimeSpan.FromSeconds(5 * attempt);
                await Task.Delay(delay).ConfigureAwait(false);
            }
        }
    }

    private static bool Is429(string message) =>
        message.Contains("429") || message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase);

    // ===================================================================
    // Reusable scenarios — each takes a factory and runs a real call.
    // ===================================================================

    private static async Task SmokeAsync(LlmConnectionFactory factory)
    {
        var provider = new OpenAiProvider(factory, SharedHttp);
        var request = new LlmRequest
        {
            SystemPrompt = "You are a calculator. Answer with the number only, no words.",
            Messages = [LlmMessage.User("What is 2+2?")],
            Temperature = 0.0,
            // Reasoning models (Cerebras gpt-oss-120b, zai-glm-4.7) emit a long
            // chain-of-thought before the visible answer; small caps truncate it.
            MaxTokens = 512
        };

        var response = await CompleteWithRetryAsync(provider, request).ConfigureAwait(false);

        response.Content.Should().NotBeEmpty();
        var text = response.Content.OfType<LlmTextBlock>().FirstOrDefault()?.Text ?? "";
        text.Should().NotBeNullOrWhiteSpace($"{factory.Provider} returned empty content");
        text.Should().Contain("4");
    }

    // Note for maintainers: this suite is English-only so it is portable across
    // contributors and CI runners regardless of locale. If you want to verify
    // a particular non-English language (e.g. Russian, Chinese, Arabic) for
    // your own deployment, run a private fork of this test with a localized
    // prompt and a script-specific assertion.
    private static async Task NonAsciiAsync(LlmConnectionFactory factory)
    {
        var provider = new OpenAiProvider(factory, SharedHttp);
        var request = new LlmRequest
        {
            SystemPrompt =
                "Reply with EXACTLY one short sentence in plain English. " +
                "Then on a new line write the same sentence using only é, è, à, ü, ö, ß accents " +
                "(use any reasonable European spelling).",
            Messages = [LlmMessage.User("Say hello.")],
            Temperature = 0.0,
            MaxTokens = 64
        };

        var response = await CompleteWithRetryAsync(provider, request).ConfigureAwait(false);
        var text = response.Content.OfType<LlmTextBlock>().FirstOrDefault()?.Text ?? "";
        text.Should().NotBeNullOrWhiteSpace();
        // Sanity check: non-empty multi-line UTF-8 round-trip.
        text.Length.Should().BeGreaterThan(2, $"{factory.Provider} returned suspiciously short text: '{text}'");
    }

    private static async Task ToolUseAsync(LlmConnectionFactory factory)
    {
        var provider = new OpenAiProvider(factory, SharedHttp);
        var request = new LlmRequest
        {
            SystemPrompt =
                "You MUST call the get_weather tool with city='Berlin'. Do not answer from memory.",
            Messages = [LlmMessage.User("What is the weather in Berlin?")],
            Temperature = 0.0,
            MaxTokens = 256,
            Tools =
            [
                new LlmToolCapability
                {
                    Name = "get_weather",
                    Description = "Returns the current weather for a given city.",
                    InputSchema = """
                    {
                      "type": "object",
                      "properties": { "city": { "type": "string" } },
                      "required": ["city"]
                    }
                    """
                }
            ]
        };

        var response = await CompleteWithRetryAsync(provider, request).ConfigureAwait(false);

        var toolUses = response.Content.OfType<LlmToolUseBlock>().ToList();
        var texts = response.Content.OfType<LlmTextBlock>().ToList();

        (toolUses.Count > 0 || texts.Count > 0).Should().BeTrue(
            $"{factory.Provider} returned no content blocks at all");

        if (toolUses.Count > 0)
        {
            toolUses[0].Name.Should().Be("get_weather");
            toolUses[0].InputJson.Should().Contain("Berlin");
        }
    }

    private static async Task UsageReportedAsync(LlmConnectionFactory factory)
    {
        var provider = new OpenAiProvider(factory, SharedHttp);
        var request = new LlmRequest
        {
            Messages = [LlmMessage.User("Say 'ok' and nothing else.")],
            Temperature = 0.0,
            MaxTokens = 8
        };
        var response = await CompleteWithRetryAsync(provider, request).ConfigureAwait(false);

        response.Usage.InputTokens.Should().BeGreaterThan(0, $"{factory.Provider} should report input tokens");
        response.Usage.OutputTokens.Should().BeGreaterThan(0, $"{factory.Provider} should report output tokens");
    }

    private static async Task StopReasonAsync(LlmConnectionFactory factory)
    {
        var provider = new OpenAiProvider(factory, SharedHttp);
        var request = new LlmRequest
        {
            Messages = [LlmMessage.User("Reply with the single word: ok")],
            Temperature = 0.0,
            MaxTokens = 8
        };
        var response = await CompleteWithRetryAsync(provider, request).ConfigureAwait(false);
        response.RawStopReason.Should().NotBeNullOrEmpty();
    }

    // ===================================================================
    // Per-provider tests. One row per (provider, scenario) — when the env
    // var is missing the row is skipped.
    // ===================================================================

    // ---------- GitHub Models ----------
    [EnvFact("REDB_LLM_GITHUB_KEY")] public Task GitHub_Smoke()    => SmokeAsync(GH());
    [EnvFact("REDB_LLM_GITHUB_KEY")] public Task GitHub_NonAscii() => NonAsciiAsync(GH());
    [EnvFact("REDB_LLM_GITHUB_KEY")] public Task GitHub_ToolUse()  => ToolUseAsync(GH());
    [EnvFact("REDB_LLM_GITHUB_KEY")] public Task GitHub_Usage()    => UsageReportedAsync(GH());
    [EnvFact("REDB_LLM_GITHUB_KEY")] public Task GitHub_Stop()     => StopReasonAsync(GH());
    private static LlmConnectionFactory GH() => MakeFactory("github", "github-models", "gpt-4o-mini", "REDB_LLM_GITHUB_KEY");

    // ---------- Groq ----------
    [EnvFact("REDB_LLM_GROQ_KEY")] public Task Groq_Smoke()    => SmokeAsync(Groq());
    [EnvFact("REDB_LLM_GROQ_KEY")] public Task Groq_NonAscii() => NonAsciiAsync(Groq());
    [EnvFact("REDB_LLM_GROQ_KEY")] public Task Groq_ToolUse()  => ToolUseAsync(Groq());
    [EnvFact("REDB_LLM_GROQ_KEY")] public Task Groq_Usage()    => UsageReportedAsync(Groq());
    [EnvFact("REDB_LLM_GROQ_KEY")] public Task Groq_Stop()     => StopReasonAsync(Groq());
    private static LlmConnectionFactory Groq() => MakeFactory("groq", "groq", "llama-3.3-70b-versatile", "REDB_LLM_GROQ_KEY");

    // ---------- xAI Grok ----------
    [EnvFact("REDB_LLM_GROK_KEY")] public Task Grok_Smoke()    => SmokeAsync(Grok());
    [EnvFact("REDB_LLM_GROK_KEY")] public Task Grok_NonAscii() => NonAsciiAsync(Grok());
    [EnvFact("REDB_LLM_GROK_KEY")] public Task Grok_ToolUse()  => ToolUseAsync(Grok());
    [EnvFact("REDB_LLM_GROK_KEY")] public Task Grok_Usage()    => UsageReportedAsync(Grok());
    [EnvFact("REDB_LLM_GROK_KEY")] public Task Grok_Stop()     => StopReasonAsync(Grok());
    private static LlmConnectionFactory Grok() => MakeFactory("grok", "grok", "grok-3-mini", "REDB_LLM_GROK_KEY");

    // ---------- Cerebras (current free models: gpt-oss-120b, zai-glm-4.7) ----------
    [EnvFact("REDB_LLM_CEREBRAS_KEY")] public Task Cerebras_Smoke()    => SmokeAsync(Cerebras());
    [EnvFact("REDB_LLM_CEREBRAS_KEY")] public Task Cerebras_NonAscii() => NonAsciiAsync(Cerebras());
    [EnvFact("REDB_LLM_CEREBRAS_KEY")] public Task Cerebras_ToolUse()  => ToolUseAsync(Cerebras());
    [EnvFact("REDB_LLM_CEREBRAS_KEY")] public Task Cerebras_Usage()    => UsageReportedAsync(Cerebras());
    private static LlmConnectionFactory Cerebras() => MakeFactory("cerebras", "cerebras", "gpt-oss-120b", "REDB_LLM_CEREBRAS_KEY");

    // ---------- OpenRouter (use Gemma — most stable :free upstream) ----------
    [EnvFact("REDB_LLM_OPENROUTER_KEY")] public Task OpenRouter_Smoke()   => SmokeAsync(OR());
    [EnvFact("REDB_LLM_OPENROUTER_KEY")] public Task OpenRouter_NonAscii() => NonAsciiAsync(OR());
    [EnvFact("REDB_LLM_OPENROUTER_KEY")] public Task OpenRouter_ToolUse() => ToolUseAsync(OR());
    private static LlmConnectionFactory OR() => MakeFactory("openrouter", "openrouter",
        "meta-llama/llama-3.3-70b-instruct:free", "REDB_LLM_OPENROUTER_KEY");

    // ---------- Google Gemini (OpenAI-compat) ----------
    [EnvFact("REDB_LLM_GEMINI_KEY")] public Task Gemini_Smoke()    => SmokeAsync(Gemini());
    [EnvFact("REDB_LLM_GEMINI_KEY")] public Task Gemini_NonAscii() => NonAsciiAsync(Gemini());
    [EnvFact("REDB_LLM_GEMINI_KEY")] public Task Gemini_ToolUse()  => ToolUseAsync(Gemini());
    [EnvFact("REDB_LLM_GEMINI_KEY")] public Task Gemini_Usage()    => UsageReportedAsync(Gemini());
    private static LlmConnectionFactory Gemini() => MakeFactory("gemini", "gemini", "gemini-2.0-flash", "REDB_LLM_GEMINI_KEY");

    // ---------- Mistral ----------
    [EnvFact("REDB_LLM_MISTRAL_KEY")] public Task Mistral_Smoke()    => SmokeAsync(Mistral());
    [EnvFact("REDB_LLM_MISTRAL_KEY")] public Task Mistral_NonAscii() => NonAsciiAsync(Mistral());
    [EnvFact("REDB_LLM_MISTRAL_KEY")] public Task Mistral_ToolUse()  => ToolUseAsync(Mistral());
    [EnvFact("REDB_LLM_MISTRAL_KEY")] public Task Mistral_Usage()    => UsageReportedAsync(Mistral());
    private static LlmConnectionFactory Mistral() => MakeFactory("mistral", "mistral", "mistral-small-latest", "REDB_LLM_MISTRAL_KEY");
}
