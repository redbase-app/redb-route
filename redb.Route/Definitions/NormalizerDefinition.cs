using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Definition for a Normalizer step. Collects when-clauses and builds a
/// <see cref="ChoiceProcessor"/> with transform branches at route-build time.
/// </summary>
public sealed class NormalizerDefinition : ProcessorDefinition, INormalizerDefinition
{
    private readonly List<(Func<IExchange, bool> Predicate, Func<IExchange, object?> Transform)> _clauses = new();
    private Func<IExchange, object?>? _otherwise;

    /// <inheritdoc />
    public INormalizerDefinition When(
        Func<IExchange, bool> predicate,
        Func<IExchange, object?> transform)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(transform);
        _clauses.Add((predicate, transform));
        return this;
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public INormalizerDefinition Otherwise(Func<IExchange, object?> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        _otherwise = transform;
        return this;
    }

    /// <summary>True when no When-clauses have been added yet (used for eager validation in v2 DSL).</summary>
    internal bool IsEmpty => _clauses.Count == 0;

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        if (_clauses.Count == 0)
            throw new InvalidOperationException("Normalizer requires at least one When clause.");

        var choice = new ChoiceProcessor();
        foreach (var (predicate, transform) in _clauses)
        {
            var t = transform;
            choice.When(predicate, new DelegateProcessor(e => { e.In.Body = t(e); }));
        }
        if (_otherwise is not null)
        {
            var otherwise = _otherwise;
            choice.SetOtherwise(new DelegateProcessor(e => { e.In.Body = otherwise(e); }));
        }
        return choice;
    }
}
