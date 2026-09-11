using redb.Route.Abstractions;
using redb.Route.Definitions;

namespace redb.Route.TestKit;

/// <summary>
/// The steps selected by <see cref="AdviceWithBuilder.WeaveById"/>, <see cref="AdviceWithBuilder.WeaveByToUri"/>
/// or <see cref="AdviceWithBuilder.WeaveByType{T}"/>, ready to be replaced, wrapped or removed
/// (Apache Camel <c>weaveById(...).replace() / .before() / .after() / .remove()</c>).
/// New steps are written with the ordinary route DSL: <c>t.Replace(r => r.To("mock://x"))</c>.
/// </summary>
public sealed class WeaveTarget
{
    private readonly AdviceWithBuilder _owner;
    private readonly IReadOnlyList<DefinitionSlot> _slots;

    internal WeaveTarget(AdviceWithBuilder owner, IReadOnlyList<DefinitionSlot> slots)
    {
        _owner = owner;
        _slots = slots;
    }

    /// <summary>Number of steps selected.</summary>
    public int Count => _slots.Count;

    /// <summary>Replaces each selected step with the steps built by <paramref name="steps"/>.</summary>
    public AdviceWithBuilder Replace(Action<IRouteDefinition> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        foreach (var slot in Descending())
            DefinitionTree.Splice(slot.Owner, slot.Index, Build(steps));
        return _owner;
    }

    /// <summary>Inserts the steps built by <paramref name="steps"/> before each selected step.</summary>
    public AdviceWithBuilder Before(Action<IRouteDefinition> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        foreach (var slot in Descending())
            DefinitionTree.InsertAll(slot.Owner, slot.Index, Build(steps));
        return _owner;
    }

    /// <summary>Inserts the steps built by <paramref name="steps"/> after each selected step.</summary>
    public AdviceWithBuilder After(Action<IRouteDefinition> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        foreach (var slot in Descending())
            DefinitionTree.InsertAll(slot.Owner, slot.Index + 1, Build(steps));
        return _owner;
    }

    /// <summary>Removes each selected step.</summary>
    public AdviceWithBuilder Remove()
    {
        foreach (var slot in Descending())
            slot.Owner.Outputs.RemoveAt(slot.Index);
        return _owner;
    }

    /// <summary>Builds steps on a scratch route so the full DSL is available, then hands over its outputs.</summary>
    internal static IReadOnlyList<IProcessorDefinition> Build(Action<IRouteDefinition> steps)
    {
        var scratch = new RouteDefinition();
        steps(scratch);
        return scratch.Outputs.ToArray();
    }

    // Mutate from the back of each owner list so earlier indices stay valid.
    private IEnumerable<DefinitionSlot> Descending() => _slots.OrderByDescending(s => s.Index);
}
