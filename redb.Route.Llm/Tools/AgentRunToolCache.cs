namespace redb.Route.Llm.Tools;

/// <summary>
/// Run-scoped memo store for <c>ToolCachingPolicy.Memoize</c>. Created when an agent run starts and
/// dropped when it returns, which is the whole of the promise: "memoised for the lifetime of the
/// agent run". No TTL, no eviction, no database row — entries cannot outlive the run that wrote
/// them, so there is nothing left over to purge.
/// <para>
/// The run loop dispatches tools sequentially, so a plain dictionary is safe; it is not shared
/// between runs.
/// </para>
/// </summary>
internal sealed class AgentRunToolCache
{
    private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);

    /// <summary>Returns the memoised output for <paramref name="key"/>, or <c>null</c>.</summary>
    public string? Get(string key) => _entries.TryGetValue(key, out var value) ? value : null;

    /// <summary>Memoises <paramref name="outputJson"/> under <paramref name="key"/>.</summary>
    public void Set(string key, string outputJson) => _entries[key] = outputJson;
}
