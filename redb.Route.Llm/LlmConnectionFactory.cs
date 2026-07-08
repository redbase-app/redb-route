using redb.Route.Llm.Providers;

namespace redb.Route.Llm;

/// <summary>
/// Connection factory configuration for the LLM connector. Register in the route
/// registry and reference by name in endpoint URIs (<c>connectionFactory=myFactory</c>
/// or as the URI host: <c>llm://myFactory</c>).
/// <para>
/// Centralises model identity, version, credentials and per-provider tuning so
/// they are kept out of the URI.
/// </para>
/// </summary>
public sealed class LlmConnectionFactory
{
    /// <summary>
    /// Logical name of this factory (the registry key, e.g. <c>"claude"</c>).
    /// Set by <see cref="Extensions.LlmServiceCollectionExtensions"/> when the factory is registered.
    /// Surfaced as the <c>llm.factory</c> tag on metrics and traces.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Provider identifier: "anthropic", "openai", "azure-openai", "ollama", "stub".</summary>
    public string Provider { get; set; } = "stub";

    /// <summary>Model identifier passed to the provider (e.g. "claude-haiku-4.5", "gpt-4o-mini").</summary>
    public string ModelId { get; set; } = "stub-model";

    /// <summary>Optional pinned model version for deterministic deploys.</summary>
    public string? ModelVersion { get; set; }

    /// <summary>API key. Prefer <see cref="ApiKeySecretRef"/> for production deployments.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Reference to a secret store entry — resolved at <see cref="Build"/> time.</summary>
    public string? ApiKeySecretRef { get; set; }

    /// <summary>Optional base URL override (Azure OpenAI, OpenAI-compatible servers, local Ollama).</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>Default sampling temperature.</summary>
    public double? Temperature { get; set; }

    /// <summary>Default max output tokens.</summary>
    public int? MaxTokens { get; set; }

    /// <summary>Default top-p sampling parameter.</summary>
    public double? TopP { get; set; }

    /// <summary>Per-call request timeout in milliseconds (default: 120 000).</summary>
    public int RequestTimeoutMs { get; set; } = 120_000;

    /// <summary>Number of automatic retries on transient errors (default: 2).</summary>
    public int Retries { get; set; } = 2;

    /// <summary>
    /// Optional pre-built provider instance. When set, <see cref="Build"/> returns it
    /// without attempting to construct one — useful for tests and custom providers.
    /// </summary>
    public ILlmProvider? PrebuiltProvider { get; set; }

    /// <summary>
    /// Builds an <see cref="ILlmProvider"/> from this configuration. Resolution order:
    /// <list type="number">
    ///   <item><see cref="PrebuiltProvider"/> if set;</item>
    ///   <item>built-in provider matching <see cref="Provider"/>;</item>
    ///   <item><see cref="NotSupportedException"/> for unknown providers.</item>
    /// </list>
    /// </summary>
    public ILlmProvider Build()
    {
        if (PrebuiltProvider is not null) return PrebuiltProvider;

        return Provider?.ToLowerInvariant() switch
        {
            "stub" => new StubProvider(this),
            "anthropic" => new AnthropicProvider(this),

            // All providers below speak the OpenAI Chat Completions protocol.
            // Default base URLs are resolved by OpenAiProvider.ResolveDefaultBaseUrl;
            // override LlmConnectionFactory.BaseUrl for self-hosted/proxy setups.
            "openai" or
            "groq" or
            "cerebras" or
            "openrouter" or
            "gemini" or "google" or
            "github-models" or "github" or
            "mistral" or
            "together" or "togetherai" or
            "huggingface" or "hf" or
            "deepseek" or
            "ollama" or
            "lmstudio"
                => OpenAiProvider.Create(this),

            _ => throw new NotSupportedException(
                $"Unknown LLM provider '{Provider}'. Built-in providers: stub, anthropic, " +
                $"openai, groq, cerebras, openrouter, gemini, github-models, mistral, together, " +
                $"huggingface, deepseek, ollama, lmstudio. " +
                $"Use {nameof(PrebuiltProvider)} to plug a custom provider.")
        };
    }
}
