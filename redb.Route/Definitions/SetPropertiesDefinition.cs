using redb.Route.Abstractions;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Sets several exchange properties in one step (one Message History entry), the property twin of
/// <see cref="SetHeadersDefinition"/>. A value that is an <see cref="IExpression"/> is evaluated per
/// message, a <c>Func&lt;IExchange, object?&gt;</c> is invoked, anything else is a constant. The
/// properties are set in order, so a later value may read an earlier one.
/// </summary>
public sealed class SetPropertiesDefinition : ProcessorDefinition
{
    /// <summary>Creates the node.</summary>
    public SetPropertiesDefinition(IReadOnlyList<(string Name, object? Value)> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        if (properties.Count == 0) throw new ArgumentException("At least one property is required.", nameof(properties));
        foreach (var (name, _) in properties)
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Properties = properties;
    }

    /// <summary>Property names and value sources.</summary>
    public IReadOnlyList<(string Name, object? Value)> Properties { get; }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new DelegateProcessor(exchange =>
        {
            foreach (var (name, value) in Properties)
                exchange.Properties[name] = value switch
                {
                    IExpression expression => expression.Evaluate<object>(exchange),
                    Func<IExchange, object?> factory => factory(exchange),
                    _ => value,
                };
        });
}
