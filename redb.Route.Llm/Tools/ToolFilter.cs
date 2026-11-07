using redb.Route.Llm.Abstractions.Tools;

namespace redb.Route.Llm.Tools;

/// <summary>
/// Resolves the per-call tool list from a registry given a CSV/wildcard filter.
/// Centralises the parsing rules so URI options, the inline DSL builder and the
/// consumer all behave the same.
/// </summary>
internal static class ToolFilter
{
    /// <summary>
    /// Resolves the tool list for a single call.
    /// </summary>
    /// <param name="registry">Registry to read from. May be <c>null</c> when DI is incomplete.</param>
    /// <param name="filter">
    /// <c>null</c>/empty \u2014 no tools (default; explicit opt-in);
    /// <c>"*"</c> \u2014 every descriptor in the registry;
    /// CSV of names \u2014 only the named descriptors (missing names are skipped silently).
    /// </param>
    public static IReadOnlyList<ILlmToolDescriptor> Resolve(
        IToolDescriptorRegistry? registry,
        string? filter)
    {
        if (registry is null) return [];
        if (string.IsNullOrWhiteSpace(filter)) return [];

        if (filter.Trim() == "*") return registry.All();

        var names = filter
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (names.Length == 0) return [];

        var result = new List<ILlmToolDescriptor>(names.Length);
        foreach (var name in names)
        {
            var descriptor = registry.Get(name);
            if (descriptor is not null) result.Add(descriptor);
        }
        return result;
    }
}
