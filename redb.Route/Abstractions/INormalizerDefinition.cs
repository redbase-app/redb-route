using System;

namespace redb.Route.Abstractions;

/// <summary>
/// Fluent builder for configuring a Normalizer step.
/// Maps different input formats to a single canonical form using predicate-based routing.
/// </summary>
public interface INormalizerDefinition
{
    /// <summary>When the predicate matches, apply the transform to normalize the body.</summary>
    /// <param name="predicate">Condition to evaluate against the exchange.</param>
    /// <param name="transform">Transform function producing the normalized body.</param>
    INormalizerDefinition When(
        Func<IExchange, bool> predicate,
        Func<IExchange, object?> transform);

    /// <summary>When the content type matches, apply the transform to normalize the body.</summary>
    /// <param name="contentType">Expected content type value.</param>
    /// <param name="transform">Transform function producing the normalized body.</param>
    INormalizerDefinition WhenContentType(
        string contentType,
        Func<IExchange, object?> transform);

    /// <summary>Default transform when no predicate matches.</summary>
    /// <param name="transform">Fallback transform function.</param>
    INormalizerDefinition Otherwise(Func<IExchange, object?> transform);
}
