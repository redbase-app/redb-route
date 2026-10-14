using System.Globalization;
using System.Text.RegularExpressions;
using redb.Route.Abstractions;

namespace redb.Route.Telemetry;

/// <summary>
/// The one place that decides what a <c>.SpanTag(...)</c> step may write: the key, checked when the route is built,
/// and the value, normalized per message. The length of a value is left to the OpenTelemetry SDK
/// (<c>OTEL_ATTRIBUTE_VALUE_LENGTH_LIMIT</c>), as for every other attribute of the engine.
/// </summary>
internal static class SpanTagPolicy
{
    // The namespaces of the engine's own attributes and of the semantic conventions it emits: a step must not
    // overwrite them (the route span stamps redb.route.id again after its pipeline, so a step's value would not even
    // survive) or pass a business value off as a convention.
    private static readonly string[] ReservedPrefixes =
        ["redb.", "messaging.", "http.", "rpc.", "db.", "exception.", "otel."];

    // OpenTelemetry attribute naming: lowercase, dot-separated namespaces.
    private static readonly Regex KeyPattern =
        new(@"^[a-z0-9_]+(\.[a-z0-9_]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Refuses a key the step must not write; called when the route is built.</summary>
    /// <param name="key">The attribute name.</param>
    /// <exception cref="ArgumentException">The key is empty, templated, malformed, reserved or names a secret.</exception>
    internal static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Contains("${", StringComparison.Ordinal))
            throw new ArgumentException(
                $"SpanTag key must be a constant (got template '{key}'); the value is where the message goes.", nameof(key));
        if (!KeyPattern.IsMatch(key))
            throw new ArgumentException(
                $"SpanTag key '{key}' is not an OpenTelemetry attribute name: lowercase letters, digits and '_', " +
                "in dot-separated segments (e.g. 'sap.order.id').", nameof(key));
        foreach (var prefix in ReservedPrefixes)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                throw new ArgumentException(
                    $"SpanTag key '{key}' is in the reserved namespace '{prefix}' of the engine and the semantic " +
                    "conventions; use a namespace of your own (e.g. 'sap.order.id').", nameof(key));
        }
        if (EndpointUri.IsSensitiveKey(key))
            throw new ArgumentException(
                $"SpanTag key '{key}' names a secret; an attribute is exported unfiltered and must not carry one.",
                nameof(key));
    }

    /// <summary>
    /// The attribute value for an evaluated <paramref name="value"/>: strings, booleans, integers and doubles as they
    /// are, a date in ISO 8601, anything else as invariant text; <c>null</c> when there is nothing to set.
    /// </summary>
    internal static object? ToAttributeValue(object? value) => value switch
    {
        null => null,
        string s => s.Length == 0 ? null : s,
        bool or int or long or double => value,
        DateTimeOffset d => d.ToString("O", CultureInfo.InvariantCulture),
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => NonEmpty(f.ToString(null, CultureInfo.InvariantCulture)),
        _ => NonEmpty(value.ToString()),
    };

    private static string? NonEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;
}
