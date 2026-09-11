using redb.Route.Abstractions;

namespace redb.Route.Definitions;

/// <summary>
/// Base class for all processor definitions in the canonical Camel ProcessorDefinition model.
/// Every EIP node derives from this and overrides <see cref="CreateProcessor"/> to produce
/// its runtime <see cref="IProcessor"/>.
/// </summary>
public abstract class ProcessorDefinition : IProcessorDefinition
{
    /// <summary>
    /// Optional step id assigned via the <c>Id("...")</c> DSL verb (Apache Camel <c>id()</c>). Used by
    /// AdviceWith weaving (<c>WeaveById</c>), diagnostics and the XML form; <c>null</c> when unnamed.
    /// When set, it becomes the node id in message history instead of the generated label+counter one.
    /// </summary>
    public string? StepId { get; internal set; }

    /// <summary>
    /// Optional human-readable step label assigned via the <c>Description("...")</c> DSL verb (Apache
    /// Camel <c>description()</c>) or the XML <c>description</c> attribute. When set, it replaces the
    /// type-derived label in message history; the node id is unaffected. <c>null</c> when unset.
    /// </summary>
    public string? StepDescription { get; internal set; }

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
