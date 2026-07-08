namespace redb.Route.Llm.Abstractions.Tools;

/// <summary>
/// Marks a class, method, or route definition as exposable to an LLM as a tool.
/// Discovered by the engine's tool registrars; the attribute carries the same
/// metadata as <see cref="LlmToolCapability"/> so a descriptor can be built
/// without reflecting on the handler body.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Property,
    AllowMultiple = false, Inherited = false)]
public sealed class ExposeAsLlmToolAttribute : Attribute
{
    /// <summary>Stable tool name used by the model. Required.</summary>
    public string Name { get; }

    /// <summary>Human-readable tool description shown to the model. Required.</summary>
    public string Description { get; }

    /// <summary>JSON Schema string for input parameters. Optional — when omitted the engine assigns a permissive object schema.</summary>
    public string? InputSchema { get; init; }

    /// <summary>Side-effect classification. Drives approval and idempotency policies.</summary>
    public ToolSideEffect SideEffect { get; init; } = ToolSideEffect.ReadOnly;

    /// <summary>Cost class hint for budget-aware scheduling.</summary>
    public ToolCostClass Cost { get; init; } = ToolCostClass.Cheap;

    /// <summary>Caching policy hint.</summary>
    public ToolCachingPolicy Caching { get; init; } = ToolCachingPolicy.None;

    /// <summary>When true the tool always requires explicit user approval before execution.</summary>
    public bool RequiresApproval { get; init; }

    /// <summary>Comma-separated list of claims required on the calling principal. Empty = no requirements.</summary>
    public string RequiredClaims { get; init; } = "";

    /// <summary>Creates a tool exposure attribute.</summary>
    /// <param name="name">Stable tool name (regex <c>[a-zA-Z][a-zA-Z0-9_]{0,63}</c>).</param>
    /// <param name="description">Human-readable description for the model.</param>
    public ExposeAsLlmToolAttribute(string name, string description)
    {
        Name = name;
        Description = description;
    }

    /// <summary>Builds an <see cref="LlmToolCapability"/> from this attribute, applying
    /// <paramref name="defaultInputSchema"/> when <see cref="InputSchema"/> is null.</summary>
    public LlmToolCapability ToCapability(string defaultInputSchema) => new()
    {
        Name = Name,
        Description = Description,
        InputSchema = InputSchema ?? defaultInputSchema,
        Safety = new LlmToolSafety
        {
            SideEffect = SideEffect,
            Cost = Cost,
            Caching = Caching,
            RequiresApproval = RequiresApproval,
            RequiredClaims = string.IsNullOrWhiteSpace(RequiredClaims)
                ? []
                : RequiredClaims
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        }
    };
}
