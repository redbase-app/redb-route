using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Leaf definition that sets the exchange body to a static value.
/// </summary>
public sealed class SetBodyStaticDefinition : ProcessorDefinition
{
    private readonly object? _value;

    /// <summary>Creates a set-body definition with a static value.</summary>
    public SetBodyStaticDefinition(object? value) { _value = value; }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new DelegateProcessor(exchange => exchange.In.Body = _value);
}

/// <summary>
/// Leaf definition that sets the exchange body using a factory function.
/// </summary>
public sealed class SetBodyFactoryDefinition : ProcessorDefinition
{
    private readonly Func<IExchange, object?> _factory;

    /// <summary>Creates a set-body definition using a factory.</summary>
    public SetBodyFactoryDefinition(Func<IExchange, object?> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new DelegateProcessor(exchange => exchange.In.Body = _factory(exchange));
}

/// <summary>
/// Leaf definition that sets the exchange body using an <see cref="IExpression"/>.
/// </summary>
public sealed class SetBodyExpressionDefinition : ProcessorDefinition
{
    private readonly IExpression _expression;

    /// <summary>The expression producing the body value.</summary>
    public IExpression Expression => _expression;

    /// <summary>Creates a set-body definition from an expression.</summary>
    public SetBodyExpressionDefinition(IExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        _expression = expression;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new ExpressionBodyProcessor(_expression);
}

/// <summary>
/// Leaf definition that sets the exchange body using a string template expression.
/// </summary>
public sealed class SetBodyStringExpressionDefinition : ProcessorDefinition
{
    private readonly string _template;

    /// <summary>The template string used to produce the body value.</summary>
    public string Template => _template;

    /// <summary>Creates a set-body definition from a string template.</summary>
    public SetBodyStringExpressionDefinition(string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        _template = template;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new StringExpressionBodyProcessor(_template);
}
