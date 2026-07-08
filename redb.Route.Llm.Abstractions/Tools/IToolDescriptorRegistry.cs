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
    public void Register(ILlmToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        _descriptors[descriptor.Capability.Name] = descriptor;
    }

    /// <inheritdoc />
    public ILlmToolDescriptor? Get(string name) =>
        _descriptors.TryGetValue(name, out var d) ? d : null;

    /// <inheritdoc />
    public IReadOnlyList<ILlmToolDescriptor> All() => [.. _descriptors.Values];
}
