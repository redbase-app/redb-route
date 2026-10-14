using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Telemetry;

namespace redb.Route.Definitions;

/// <summary>
/// Leaf definition of <c>.SpanTag(key, value, target)</c>: one attribute on a span of the engine, so a trace can be
/// found by a business key. The key is checked when the route is built (<c>SpanTagPolicy</c>); the value is an
/// <see cref="IExpression"/>, a literal being a constant one.
/// </summary>
public sealed class SpanTagDefinition : ProcessorDefinition
{
    /// <summary>Creates a span-tag definition.</summary>
    /// <param name="key">Attribute name: a constant, lowercase, dot-separated, outside the reserved namespaces.</param>
    /// <param name="value">Expression producing the value, evaluated per message.</param>
    /// <param name="target">Which span receives the attribute.</param>
    /// <exception cref="ArgumentException"><paramref name="key"/> is refused by the policy.</exception>
    public SpanTagDefinition(string key, IExpression value, SpanTarget target)
    {
        SpanTagPolicy.ValidateKey(key);
        ArgumentNullException.ThrowIfNull(value);
        if (!Enum.IsDefined(target))
            throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown span target.");
        Key = key;
        Value = value;
        Target = target;
    }

    /// <summary>The attribute name.</summary>
    public string Key { get; }

    /// <summary>The expression producing the attribute value.</summary>
    public IExpression Value { get; }

    /// <summary>Which span receives the attribute.</summary>
    public SpanTarget Target { get; }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new SpanTagProcessor(Key, Value, Target,
            context.GetService<ILoggerFactory>()?.CreateLogger<SpanTagProcessor>());
}
