using System.Collections.Concurrent;

namespace redb.Route.Llm.Abstractions.Tools;

/// <summary>
/// Registry of tool descriptors available to the agent engine. Resolved by name at dispatch time.
/// Descriptors registered via DSL (<c>.AsLlmTool(...)</c> / <c>LlmTool.Define(...)</c>) and
/// the <c>[ExposeAsLlmTool]</c> attribute all land here.
/// </summary>
public interface IToolDescriptorRegistry
{
    /// <summary>Adds or replaces a descriptor in the registry.</summary>
    void Register(ILlmToolDescriptor descriptor);

    /// <summary>Returns a descriptor by capability name, or null if not registered.</summary>
    ILlmToolDescriptor? Get(string name);

    /// <summary>Snapshot of all currently registered descriptors.</summary>
    IReadOnlyList<ILlmToolDescriptor> All();
}

/// <summary>Default in-memory <see cref="IToolDescriptorRegistry"/>.</summary>
public sealed class ToolDescriptorRegistry : IToolDescriptorRegistry
{
    private readonly ConcurrentDictionary<string, ILlmToolDescriptor> _descriptors = new(StringComparer.Ordinal);

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// The descriptor declares a caching policy on a tool that is not read-only. Every descriptor path
    /// converges here — the <c>.AsLlmTool(...)</c> DSL, <c>LlmTool.Define(...).Build()</c>, the
    /// <c>[ExposeAsLlmTool]</c> attribute and MCP discovery — so the rule is enforced once, for all of
    /// them, instead of only where the route author happens to use the DSL.
    /// </exception>
    public void Register(ILlmToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        // A cached entry answers a call without running the tool, so caching a mutating tool suppresses
        // the side effect it stands for — up to the whole TTL, including legitimate repeats
        // ("send that invoice again"). Only a read-only tool may declare a caching policy.
        var safety = descriptor.Capability.Safety;
        if (safety.Caching != ToolCachingPolicy.None && safety.SideEffect != ToolSideEffect.ReadOnly)
            throw new InvalidOperationException(
                $"Tool '{descriptor.Capability.Name}' declares Caching={safety.Caching} with "
                + $"SideEffect={safety.SideEffect}. Only read-only tools may be cached — a cache hit would "
                + "suppress the side effect.");

        _descriptors[descriptor.Capability.Name] = descriptor;
    }

    /// <inheritdoc />
    public ILlmToolDescriptor? Get(string name) =>
        _descriptors.TryGetValue(name, out var d) ? d : null;

    /// <inheritdoc />
    public IReadOnlyList<ILlmToolDescriptor> All() => [.. _descriptors.Values];
}
