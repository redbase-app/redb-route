namespace redb.Route.Llm.Abstractions.Tools;

/// <summary>
/// Capability descriptor for a tool exposed to the LLM. The engine projects the
/// <see cref="Name"/>, <see cref="Description"/> and <see cref="InputSchema"/> into the provider
/// request as <c>tools[]</c>; <see cref="Safety"/> is server-side metadata and is never sent on
/// the wire.
/// <para>
/// What the engine consults in <see cref="Safety"/>: <see cref="LlmToolSafety.RequiresApproval"/>
/// (the approval gate), <see cref="LlmToolSafety.RequiredClaims"/> (claims, fail closed),
/// <see cref="LlmToolSafety.Caching"/> (tool-result cache, read-only tools only) and
/// <see cref="LlmToolSafety.SideEffect"/> (replay de-duplication and the caching gate). Each field
/// states its own status; <see cref="LlmToolSafety.Cost"/> is the one still reserved.
/// </para>
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
