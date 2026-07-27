using System.Collections.Generic;
using redb.Route.Abstractions;
using redb.Route.Definitions;

namespace redb.Route.Validation;

/// <summary>
/// Validates a v2 <see cref="IRouteDefinition"/> tree before compilation. Walks
/// <see cref="IProcessorDefinition.Outputs"/> recursively and applies two kinds of rule:
/// <list type="bullet">
/// <item>Per-node self-validation (e.g. ScatterGather requires recipients).</item>
/// <item>Generic scope-nesting rules: any node implementing <see cref="IScopeNestingRule"/> is
/// evaluated against each enclosing scope (a node implementing <see cref="IRouteScope"/>). A
/// <see cref="NestingPolicy.Forbid"/> becomes a build error, a <see cref="NestingPolicy.Warn"/> a
/// warning. This is declarative — new constraints ship on the definitions, not here.</item>
/// </list>
/// Catches structural errors early rather than producing cryptic runtime failures.
/// </summary>
internal static class RouteDefinitionValidator
{
    /// <summary>
    /// Validates a route definition. Throws <see cref="RouteValidationException"/> if any errors are
    /// found; returns the (possibly empty) list of non-fatal warnings for the caller to log.
    /// </summary>
    public static IReadOnlyList<string> Validate(IRouteDefinition definition)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (definition is IProcessorDefinition root)
            ValidateTree(root, errors, warnings, new List<IProcessorDefinition>());

        if (errors.Count > 0)
            throw new RouteValidationException(definition.GetRouteId() ?? "<unnamed>", errors);

        return warnings;
    }

    private static void ValidateTree(
        IProcessorDefinition node,
        List<string> errors,
        List<string> warnings,
        List<IProcessorDefinition> enclosingScopes)
    {
        // 1. Per-node self-validation.
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

        // 2. Generic scope-nesting rules — the node declares its own policy against each enclosing scope.
        if (node is IScopeNestingRule rule)
        {
            foreach (var ancestor in enclosingScopes)
            {
                var verdict = rule.CheckAncestor(ancestor);
                switch (verdict.Policy)
                {
                    case NestingPolicy.Forbid: errors.Add(verdict.Message!); break;
                    case NestingPolicy.Warn: warnings.Add(verdict.Message!); break;
                }
            }
        }

        // 3. Descend, tracking scope-openers as enclosing scopes for nested nodes. Recurse into
        //    Outputs AND any branches a definition keeps outside Outputs (e.g. Choice When/Otherwise).
        var isScope = node is IRouteScope;
        if (isScope) enclosingScopes.Add(node);
        foreach (var child in node.Outputs)
            ValidateTree(child, errors, warnings, enclosingScopes);
        if (node is IBranchingDefinition branching)
            foreach (var branch in branching.Branches)
                ValidateTree(branch, errors, warnings, enclosingScopes);
        if (isScope) enclosingScopes.RemoveAt(enclosingScopes.Count - 1);
    }
}
