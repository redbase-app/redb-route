using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Leaf definition that transforms the exchange body using a factory function.
/// </summary>
public sealed class TransformDefinition : ProcessorDefinition
{
    private readonly Func<IExchange, object?> _transform;

    /// <summary>Creates a transform definition from a factory.</summary>
    public TransformDefinition(Func<IExchange, object?> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        _transform = transform;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new DelegateProcessor(exchange => exchange.In.Body = _transform(exchange));
}

/// <summary>
/// Leaf definition that transforms the exchange body using an <see cref="IExpression"/>.
/// </summary>
public sealed class TransformExpressionDefinition : ProcessorDefinition
{
    private readonly IExpression _expression;

    /// <summary>The expression producing the transformed body.</summary>
    public IExpression Expression => _expression;

    /// <summary>Creates a transform definition from an expression.</summary>
    public TransformExpressionDefinition(IExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        _expression = expression;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new ExpressionBodyProcessor(_expression);
}

/// <summary>
/// Leaf definition that transforms the exchange body using a string template expression.
/// </summary>
public sealed class TransformStringExpressionDefinition : ProcessorDefinition
{
    private readonly string _template;

    /// <summary>The template string used to produce the transformed body.</summary>
    public string Template => _template;

    /// <summary>Creates a transform definition from a string template.</summary>
    public TransformStringExpressionDefinition(string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        _template = template;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new StringExpressionBodyProcessor(_template);
}
