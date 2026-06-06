using System.Collections.Concurrent;

namespace redb.Route.Llm.Engine.Storage;

/// <summary>
/// Resolves a named prompt template into the actual system / user text the
/// engine should send to the provider. Versioning is required so that a
/// recorded eval run can be replayed against the exact template that produced it.
/// </summary>
public interface IPromptTemplateRegistry
{
    /// <summary>
    /// Returns the requested template. When <paramref name="version"/> is null,
    /// the latest version is returned.
    /// </summary>
    Task<PromptTemplate?> GetAsync(string name, string? version = null, CancellationToken ct = default);

    /// <summary>Registers or updates a template version.</summary>
    Task SetAsync(PromptTemplate template, CancellationToken ct = default);

    /// <summary>Enumerates every version of every template — used for tooling.</summary>
    Task<IReadOnlyList<PromptTemplate>> ListAsync(CancellationToken ct = default);
}

/// <summary>A versioned prompt template.</summary>
public sealed class PromptTemplate
{
    /// <summary>Stable template name (free of versioning).</summary>
    public required string Name { get; init; }

    /// <summary>Version string (semver, hash, or monotonically-increasing tag).</summary>
    public required string Version { get; init; }

    /// <summary>Template body — placeholder substitution is the caller's responsibility.</summary>
    public required string Body { get; init; }

    /// <summary>Optional human description.</summary>
    public string? Description { get; init; }

    /// <summary>Wall-clock timestamp the version was registered.</summary>
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>In-memory registry — registered templates live only for the process lifetime.</summary>
public sealed class InMemoryPromptTemplateRegistry : IPromptTemplateRegistry
{
    private readonly ConcurrentDictionary<string, PromptTemplate> _templates = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<PromptTemplate?> GetAsync(string name, string? version = null, CancellationToken ct = default)
    {
        if (version is not null)
        {
            return Task.FromResult(_templates.TryGetValue(Key(name, version), out var exact) ? exact : null);
        }

        var latest = _templates.Values
            .Where(t => string.Equals(t.Name, name, StringComparison.Ordinal))
            .OrderByDescending(t => t.CreatedAtUtc)
            .FirstOrDefault();
        return Task.FromResult(latest);
    }

    /// <inheritdoc />
    public Task SetAsync(PromptTemplate template, CancellationToken ct = default)
    {
        _templates[Key(template.Name, template.Version)] = template;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PromptTemplate>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PromptTemplate>>(_templates.Values.ToArray());

    private static string Key(string name, string version) => $"{name}@{version}";
}
