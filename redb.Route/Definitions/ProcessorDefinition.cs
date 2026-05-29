using redb.Route.Abstractions;

namespace redb.Route.Definitions;

/// <summary>
/// Base class for all processor definitions in the canonical Camel ProcessorDefinition model.
/// Every EIP node derives from this and overrides <see cref="CreateProcessor"/> to produce
/// its runtime <see cref="IProcessor"/>.
/// </summary>
public abstract class ProcessorDefinition : IProcessorDefinition
{
    /// <inheritdoc/>
    public IList<IProcessorDefinition> Outputs { get; } = new List<IProcessorDefinition>();

    /// <inheritdoc/>
    public IProcessorDefinition? Parent { get; set; }

    /// <inheritdoc/>
    public abstract IProcessor CreateProcessor(IRouteContext context);

    /// <summary>
    /// Appends <paramref name="output"/> to <see cref="Outputs"/> and sets its
    /// <see cref="IProcessorDefinition.Parent"/> to this node.
    /// </summary>
    protected void AddOutput(IProcessorDefinition output)
    {
        output.Parent = this;
        Outputs.Add(output);
    }
}
