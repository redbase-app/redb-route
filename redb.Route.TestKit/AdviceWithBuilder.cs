using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;

namespace redb.Route.TestKit;

/// <summary>
/// Rewrites one route definition before the context starts (Apache Camel <c>AdviceWith</c>):
/// swap the consumer, send matching <c>To</c> steps to mocks, or weave steps in, out and around
/// existing ones. The route under test is not modified for testability — the advice is.
/// Obtained through <see cref="RouteContextAdviceExtensions.AdviceRoute"/>.
/// </summary>
public sealed class AdviceWithBuilder
{
    private readonly RouteDefinition _route;

    internal AdviceWithBuilder(RouteDefinition route)
    {
        _route = route ?? throw new ArgumentNullException(nameof(route));
    }

    /// <summary>The route id being advised (explicit <c>RouteId</c> or the endpoint-derived fallback).</summary>
    public string RouteId { get; internal init; } = string.Empty;

    /// <summary>Replaces the route's <c>From</c> endpoint (Camel <c>replaceFromWith</c>): <c>kafka://orders</c> becomes <c>direct://test-in</c>.</summary>
    public AdviceWithBuilder ReplaceFrom(string uri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        _route.From(uri);
        return this;
    }

    /// <summary>
    /// Sends every static <c>To</c> whose URI matches one of <paramref name="uriMasks"/> to the mock
    /// standing in for it (<see cref="MockUri.For"/>) <b>instead of</b> the original endpoint — the
    /// original is never called. Masks: exact URI, <c>scheme://prefix*</c>, <c>regex:...</c>.
    /// </summary>
    public AdviceWithBuilder MockEndpoints(params string[] uriMasks)
    {
        if (uriMasks is not { Length: > 0 })
            throw new ArgumentException("At least one URI mask is required.", nameof(uriMasks));

        foreach (var slot in DefinitionTree.Slots(_route))
        {
            if (slot.Node is not ToDefinition to || !uriMasks.Any(mask => UriMask.IsMatch(mask, to.Uri)))
                continue;
            var mock = new ToDefinition(MockUri.For(to.Uri)) { StepId = to.StepId, Parent = slot.Owner };
            slot.Owner.Outputs[slot.Index] = mock;
        }
        return this;
    }

    /// <summary>Same as <see cref="MockEndpoints"/>; the Camel name for "mock and do not call the original", kept for recognisability.</summary>
    public AdviceWithBuilder MockEndpointsAndSkip(params string[] uriMasks) => MockEndpoints(uriMasks);

    /// <summary>Selects the steps named <paramref name="id"/> via the <c>Id("...")</c> DSL verb.</summary>
    public WeaveTarget WeaveById(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Select($"id '{id}'", s => s.Node is ProcessorDefinition { StepId: { } stepId } && string.Equals(stepId, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Selects the static <c>To</c> steps whose URI matches <paramref name="uriMask"/>.</summary>
    public WeaveTarget WeaveByToUri(string uriMask)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uriMask);
        return Select($"To URI mask '{uriMask}'", s => s.Node is ToDefinition to && UriMask.IsMatch(uriMask, to.Uri));
    }

    /// <summary>Selects every step of definition type <typeparamref name="T"/> (e.g. <c>ThrottleDefinition</c>).</summary>
    public WeaveTarget WeaveByType<T>() where T : IProcessorDefinition
        => Select($"type {typeof(T).Name}", s => s.Node is T);

    /// <summary>Adds the steps built by <paramref name="steps"/> at the very beginning of the route.</summary>
    public AdviceWithBuilder WeaveAddFirst(Action<IRouteDefinition> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        DefinitionTree.InsertAll(_route, 0, WeaveTarget.Build(steps));
        return this;
    }

    /// <summary>Adds the steps built by <paramref name="steps"/> at the very end of the route.</summary>
    public AdviceWithBuilder WeaveAddLast(Action<IRouteDefinition> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        DefinitionTree.InsertAll(_route, _route.Outputs.Count, WeaveTarget.Build(steps));
        return this;
    }

    private WeaveTarget Select(string what, Func<DefinitionSlot, bool> match)
    {
        var slots = DefinitionTree.Slots(_route).Where(match).ToArray();
        if (slots.Length == 0)
            throw new InvalidOperationException($"AdviceWith on route '{RouteId}': no step matches {what}.");
        return new WeaveTarget(this, slots);
    }
}
