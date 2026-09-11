using redb.Route.Abstractions;

namespace redb.Route.TestKit;

/// <summary>
/// A replaceable position in a route definition tree: <see cref="Node"/> is
/// <c>Owner.Outputs[Index]</c>. Branch containers reached through
/// <see cref="IBranchingDefinition.Branches"/> (a <c>When</c>, a <c>Catch</c>) are walked for their
/// own outputs but are not slots themselves — a branch is structure, not a step.
/// </summary>
internal readonly record struct DefinitionSlot(IProcessorDefinition Owner, int Index, IProcessorDefinition Node);

/// <summary>
/// The one tree walk the test kit uses: the same shape as the core validator — recurse into
/// <see cref="IProcessorDefinition.Outputs"/> and into <see cref="IBranchingDefinition.Branches"/> —
/// so <c>MockEndpoints</c> reaches a <c>To</c> inside <c>Choice</c>, <c>Split</c>, <c>Multicast</c>,
/// <c>TryCatch</c>, <c>CircuitBreaker</c> fallbacks and every other scope without per-type code.
/// </summary>
internal static class DefinitionTree
{
    /// <summary>All slots under <paramref name="root"/>, depth-first in document order (snapshot: safe to mutate while iterating).</summary>
    public static List<DefinitionSlot> Slots(IProcessorDefinition root)
    {
        var slots = new List<DefinitionSlot>();
        Collect(root, slots);
        return slots;
    }

    private static void Collect(IProcessorDefinition owner, List<DefinitionSlot> slots)
    {
        for (var i = 0; i < owner.Outputs.Count; i++)
        {
            var child = owner.Outputs[i];
            slots.Add(new DefinitionSlot(owner, i, child));
            Collect(child, slots);
        }

        if (owner is IBranchingDefinition branching)
            foreach (var branch in branching.Branches)
                Collect(branch, slots);
    }

    /// <summary>Replaces <c>owner.Outputs[index]</c> with <paramref name="replacements"/> (zero or more nodes), re-parenting them.</summary>
    public static void Splice(IProcessorDefinition owner, int index, IReadOnlyList<IProcessorDefinition> replacements)
    {
        owner.Outputs.RemoveAt(index);
        InsertAll(owner, index, replacements);
    }

    /// <summary>Inserts <paramref name="nodes"/> into <c>owner.Outputs</c> at <paramref name="index"/>, re-parenting them.</summary>
    public static void InsertAll(IProcessorDefinition owner, int index, IReadOnlyList<IProcessorDefinition> nodes)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            nodes[i].Parent = owner;
            owner.Outputs.Insert(index + i, nodes[i]);
        }
    }
}
