using redb.Route.Abstractions;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Sends the exchange to the given endpoint URI.
/// Leaf node — no <see cref="IProcessorDefinition.Outputs"/>.
/// </summary>
public sealed class ToDefinition : ProcessorDefinition
{
    private readonly string _uri;

    /// <summary>Creates a new <see cref="ToDefinition"/> for the given endpoint URI.</summary>
    public ToDefinition(string uri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        _uri = uri;
    }

    /// <inheritdoc/>
    public override IProcessor CreateProcessor(IRouteContext context)
        => new ToProcessor(_uri, context);
}
