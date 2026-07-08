using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Leaf definition that sets an exchange property to a static value.
/// </summary>
public sealed class SetPropertyStaticDefinition : ProcessorDefinition
{
    private readonly string _key;
    private readonly object? _value;

    /// <summary>Creates a set-property definition with a static value.</summary>
    public SetPropertyStaticDefinition(string key, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _key = key;
        _value = value;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new DelegateProcessor(exchange => exchange.Properties[_key] = _value);
}

/// <summary>
/// Leaf definition that sets an exchange property using a factory function.
/// </summary>
public sealed class SetPropertyFactoryDefinition : ProcessorDefinition
{
    private readonly string _key;
    private readonly Func<IExchange, object?> _factory;

    /// <summary>Creates a set-property definition using a factory.</summary>
    public SetPropertyFactoryDefinition(string key, Func<IExchange, object?> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);
        _key = key;
        _factory = factory;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new DelegateProcessor(exchange => exchange.Properties[_key] = _factory(exchange));
}

/// <summary>
/// Leaf definition that sets an exchange property using an <see cref="IExpression"/>.
/// </summary>
public sealed class SetPropertyExpressionDefinition : ProcessorDefinition
{
    private readonly string _key;
    private readonly IExpression _expression;

    /// <summary>Creates a set-property definition from an expression.</summary>
    public SetPropertyExpressionDefinition(string key, IExpression expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(expression);
        _key = key;
        _expression = expression;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new ExpressionPropertyProcessor(_key, _expression);
}

/// <summary>
/// Leaf definition that sets an exchange property using a string template expression.
/// </summary>
public sealed class SetPropertyStringExpressionDefinition : ProcessorDefinition
{
    private readonly string _key;
    private readonly string _template;

    /// <summary>Creates a set-property definition from a string template.</summary>
    public SetPropertyStringExpressionDefinition(string key, string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        _key = key;
        _template = template;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new StringExpressionPropertyProcessor(_key, _template);
}

/// <summary>
/// Leaf definition that removes a property from the exchange.
/// </summary>
public sealed class RemovePropertyDefinition : ProcessorDefinition
{
    private readonly string _key;

    /// <summary>Creates a remove-property definition.</summary>
    public RemovePropertyDefinition(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _key = key;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new DelegateProcessor(exchange => exchange.Properties.Remove(_key));
}
