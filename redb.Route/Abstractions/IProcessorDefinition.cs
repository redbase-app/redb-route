namespace redb.Route.Abstractions;

/// <summary>
/// Camel-canonical processor definition. Each EIP node implements this interface
/// and knows how to compile itself into a runtime <see cref="IProcessor"/>.
/// </summary>
public interface IProcessorDefinition
{
    /// <summary>Child nodes compiled as a linear pipeline by default.</summary>
    IList<IProcessorDefinition> Outputs { get; }

    /// <summary>Parent node in the AST; null for the root route.</summary>
    IProcessorDefinition? Parent { get; set; }

    /// <summary>Compile this node into a runtime processor.</summary>
    IProcessor CreateProcessor(IRouteContext context);
}
