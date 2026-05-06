using redb.Route.Abstractions;

namespace redb.Route.Definitions;

/// <summary>
/// Internal builder that collects normalizer when-clauses
/// and converts them into a <see cref="ChoiceStep"/> with <see cref="TransformStep"/> branches.
/// </summary>
internal sealed class NormalizerDefinition : INormalizerDefinition
{
    private readonly List<(Func<IExchange, bool> Predicate, Func<IExchange, object?> Transform)> _clauses = new();
    private Func<IExchange, object?>? _otherwise;

    public INormalizerDefinition When(
        Func<IExchange, bool> predicate,
        Func<IExchange, object?> transform)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(transform);
        _clauses.Add((predicate, transform));
        return this;
    }

    public INormalizerDefinition WhenContentType(
        string contentType,
        Func<IExchange, object?> transform)
    {
        ArgumentNullException.ThrowIfNull(contentType);
        ArgumentNullException.ThrowIfNull(transform);
        Func<IExchange, bool> predicate = e => string.Equals(
            e.In.Headers.TryGetValue("ContentType", out var ct) ? ct?.ToString() : null,
            contentType,
            StringComparison.OrdinalIgnoreCase);
        _clauses.Add((predicate, transform));
        return this;
    }

    public INormalizerDefinition Otherwise(Func<IExchange, object?> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        _otherwise = transform;
        return this;
    }

    /// <summary>
    /// Builds a <see cref="ChoiceStep"/> where each when-clause wraps a single <see cref="TransformStep"/>.
    /// </summary>
    internal ChoiceStep Build()
    {
        if (_clauses.Count == 0)
            throw new InvalidOperationException("Normalizer requires at least one When clause.");

        var whenClauses = _clauses
            .Select(c => new ChoiceWhenClause(
                c.Predicate,
                new RouteStep[] { new TransformStep(c.Transform) }))
            .ToList();

        var otherwiseSteps = _otherwise is not null
            ? new RouteStep[] { new TransformStep(_otherwise) }
            : null;

        return new ChoiceStep(whenClauses, otherwiseSteps);
    }
}
