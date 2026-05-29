using System.Collections.Generic;
using redb.Route.Abstractions;
using redb.Route.Definitions;

namespace redb.Route.Validation;

/// <summary>
/// Validates a v2 <see cref="IRouteDefinition"/> tree before compilation.
/// Walks the <see cref="IProcessorDefinition.Outputs"/> recursively and lets each
/// scope-definition validate itself via well-known public properties.
/// Catches structural errors early rather than producing cryptic runtime failures.
/// </summary>
internal static class RouteDefinitionValidator
{
    /// <summary>
    /// Validates a route definition and throws <see cref="RouteValidationException"/> if any errors are found.
    /// </summary>
    public static void Validate(IRouteDefinition definition)
    {
        var errors = new List<string>();
        if (definition is IProcessorDefinition root)
            ValidateTree(root, errors);

        if (errors.Count > 0)
            throw new RouteValidationException(definition.GetRouteId() ?? "<unnamed>", errors);
    }

    private static void ValidateTree(IProcessorDefinition node, List<string> errors)
    {
        switch (node)
        {
            case ScatterGatherDefinition sg:
                if ((sg.StaticRecipients is null || sg.StaticRecipients.Length == 0) && sg.DynamicRecipients is null)
                    errors.Add("ScatterGather: at least one recipient (static or dynamic) is required.");
                if (sg.AggregationStrategy is null)
                    errors.Add("ScatterGather: AggregationStrategy is required.");
                if (sg.MaxDegreeOfParallelism < 0)
                    errors.Add("ScatterGather: MaxDegreeOfParallelism must be >= 0.");
                break;
        }

        foreach (var child in node.Outputs)
            ValidateTree(child, errors);
    }
}
