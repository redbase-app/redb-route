using redb.Core.Attributes;

namespace redb.Route.Llm.Storage.Redb.Schemas;

/// <summary>
/// A versioned prompt template. (<see cref="Name"/>, <see cref="Version"/>)
/// is the business key; the framework loads "latest" by ordering on
/// <see cref="CreatedAtUtc"/>.
/// </summary>
[RedbScheme("LLM Prompt Template")]
public class PromptTemplateProps
{
    /// <summary>Stable template name (free of version).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Version string — semver / hash / monotonic tag.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Template body. Substitution is the caller's responsibility.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Optional human description for tooling.</summary>
    public string? Description { get; set; }

    /// <summary>When this version was registered.</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }
}
