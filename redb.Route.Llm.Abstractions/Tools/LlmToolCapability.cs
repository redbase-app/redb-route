namespace redb.Route.Llm.Abstractions.Tools;

/// <summary>
/// Capability descriptor for a tool exposed to the LLM. The agent engine projects
/// these capabilities into the provider request as <c>tools[]</c> and uses
/// <see cref="Safety"/> to enforce approval, idempotency, caching and budget policies
/// before invoking the underlying <see cref="ILlmToolDescriptor"/>.
/// </summary>
public sealed class LlmToolCapability
{
    /// <summary>Stable tool name. Must match the regex <c>[a-zA-Z][a-zA-Z0-9_]{0,63}</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable description shown to the model.</summary>
    public required string Description { get; init; }

    /// <summary>JSON Schema (object) describing the tool's input parameters.</summary>
    public required string InputSchema { get; init; }

    /// <summary>Governance metadata — side-effect, cost, caching, approval, claims.</summary>
    public LlmToolSafety Safety { get; init; } = new();
}
