namespace redb.Route.Tests.Llm.TestHelpers;

/// <summary>
/// Shared collection definition that disables intra-class parallelism for live
/// LLM tests. Free-tier endpoints (Gemini 15 RPM, Groq tier-1, OpenRouter
/// shared upstream) burn out fast under parallel load — serializing the whole
/// suite costs a few seconds and saves us from flaky 429 cascades.
/// </summary>
[CollectionDefinition("LiveLlmSerial", DisableParallelization = true)]
public sealed class LiveLlmCollection { }
