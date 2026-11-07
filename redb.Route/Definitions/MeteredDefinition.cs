using redb.Route.Abstractions;
using redb.Route.Processors;
using redb.Route.Telemetry;

namespace redb.Route.Definitions;

/// <summary>
/// Scope-opener definition for per-step metrics collection.
/// All child steps run inside a single <see cref="MeteredStepProcessor"/> that records
/// <c>redb.route.step.processed</c>, <c>redb.route.step.failed</c>,
/// and <c>redb.route.step.duration</c> tagged with <see cref="StepName"/>.
/// Inherits the leaf DSL from <see cref="RouteDefinitionBase{TSelf}"/>;
/// close with <see cref="EndMetered"/> or the universal <see cref="End"/>.
/// </summary>
public class MeteredDefinition : RouteDefinitionBase<MeteredDefinition>, IRouteScope
{
    /// <summary>Step name used as a metric tag value for <c>redb.route.step</c>.</summary>
    public string StepName { get; }

    private readonly List<(string Name, Func<IExchange, object?> Resolver)> _tagProviders = new();

    /// <summary>Tag providers evaluated per message to enrich the recorded metrics with low-cardinality dimensions.</summary>
    public IReadOnlyList<(string Name, Func<IExchange, object?> Resolver)> TagProviders => _tagProviders;

    internal MeteredDefinition(string stepName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
        StepName = stepName;
    }

    /// <summary>
    /// Adds a dimension/label evaluated per message. Use only with bounded value domains
    /// (status codes, tenant ids from a small set, etc.) to keep cardinality under control.
    /// </summary>
    public MeteredDefinition Tag(string name, Func<IExchange, object?> resolver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(resolver);
        _tagProviders.Add((name, resolver));
        return this;
    }

    /// <summary>Convenience: tag the metric with the value of a header.</summary>
    public MeteredDefinition TagFromHeader(string tagName, string headerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headerName);
        return Tag(tagName, e => e.In.Headers.TryGetValue(headerName, out var v) ? v : null);
    }

    // ── Navigation ─────────────────────────────────────────────────────────────

    /// <summary>Closes this metered scope and returns the parent route definition.</summary>
    /// <exception cref="InvalidOperationException">Thrown if no parent route is set.</exception>
    public IRouteDefinition EndMetered()
        => (IRouteDefinition)(Parent ?? throw new InvalidOperationException(
            "EndMetered() called without a parent route. Ensure Metered() was called on a route definition."));

    /// <inheritdoc cref="IRouteScope.End"/>
    public IRouteDefinition End() => EndMetered();

    // ── IProcessorDefinition ───────────────────────────────────────────────────

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        IProcessor body = BuildPipeline(Outputs, context);
        return new MeteredStepProcessor(body, StepName, _tagProviders.Count > 0 ? _tagProviders.ToArray() : null);
    }

    private static IProcessor BuildPipeline(IList<IProcessorDefinition> outputs, IRouteContext context)
        => NodePipeline.Body(context, outputs);
}
