using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Leaf definition that sets a header on the exchange to a static value.
/// </summary>
public sealed class SetHeaderStaticDefinition : ProcessorDefinition
{
    private readonly string _key;
    private readonly object? _value;

    /// <summary>Creates a set-header definition with a static value.</summary>
    public SetHeaderStaticDefinition(string key, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _key = key;
        _value = value;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new DelegateProcessor(exchange => exchange.In.Headers[_key] = _value);
}

/// <summary>
/// Leaf definition that sets a header on the exchange using a factory function.
/// </summary>
public sealed class SetHeaderFactoryDefinition : ProcessorDefinition
{
    private readonly string _key;
    private readonly Func<IExchange, object?> _factory;

    /// <summary>Creates a set-header definition using a factory.</summary>
    public SetHeaderFactoryDefinition(string key, Func<IExchange, object?> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);
        _key = key;
        _factory = factory;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new DelegateProcessor(exchange => exchange.In.Headers[_key] = _factory(exchange));
}

/// <summary>
/// Leaf definition that sets a header on the exchange using an <see cref="IExpression"/>.
/// </summary>
public sealed class SetHeaderExpressionDefinition : ProcessorDefinition
{
    private readonly string _name;
    private readonly IExpression _expression;

    /// <summary>The header name to set.</summary>
    public string Name => _name;

    /// <summary>The expression producing the header value.</summary>
    public IExpression Expression => _expression;

    /// <summary>Creates a set-header definition from an expression.</summary>
    public SetHeaderExpressionDefinition(string name, IExpression expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(expression);
        _name = name;
        _expression = expression;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new ExpressionHeaderProcessor(_name, _expression);
}

/// <summary>
/// Leaf definition that sets a header on the exchange using a string template expression.
/// </summary>
public sealed class SetHeaderStringExpressionDefinition : ProcessorDefinition
{
    private readonly string _name;
    private readonly string _template;

    /// <summary>The header name to set.</summary>
    public string Name => _name;

    /// <summary>The template string used to produce the header value.</summary>
    public string Template => _template;

    /// <summary>Creates a set-header definition from a string template.</summary>
    public SetHeaderStringExpressionDefinition(string name, string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        _name = name;
        _template = template;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new StringExpressionHeaderProcessor(_name, _template);
}

/// <summary>
/// Leaf definition that removes a header from the exchange.
/// </summary>
public sealed class RemoveHeaderDefinition : ProcessorDefinition
{
    private readonly string _key;

    /// <summary>Creates a remove-header definition.</summary>
    public RemoveHeaderDefinition(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _key = key;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new DelegateProcessor(exchange => exchange.In.Headers.Remove(_key));
}

/// <summary>
/// Leaf definition that removes the body of the exchange (sets it to null).
/// </summary>
public sealed class RemoveBodyDefinition : ProcessorDefinition
{
    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new DelegateProcessor(exchange => exchange.In.Body = null);
}
