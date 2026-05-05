using redb.Route.Abstractions;

namespace redb.Route.Expressions.Ast;

/// <summary>
/// AST node representing a ternary conditional expression (<c>condition ? ifTrue : ifFalse</c>).
/// </summary>
public class TernaryNode : AstNode
{
    /// <summary>
    /// Gets the condition expression.
    /// </summary>
    public AstNode Condition { get; }

    /// <summary>
    /// Gets the expression evaluated when the condition is true.
    /// </summary>
    public AstNode IfTrue { get; }

    /// <summary>
    /// Gets the expression evaluated when the condition is false.
    /// </summary>
    public AstNode IfFalse { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="TernaryNode"/> class.
    /// </summary>
    /// <param name="condition">The condition node.</param>
    /// <param name="ifTrue">The node evaluated when condition is true.</param>
    /// <param name="ifFalse">The node evaluated when condition is false.</param>
    public TernaryNode(AstNode condition, AstNode ifTrue, AstNode ifFalse)
    {
        Condition = condition;
        IfTrue = ifTrue;
        IfFalse = ifFalse;
    }

    /// <inheritdoc />
    public override object? Evaluate(IExchange exchange)
    {
        var conditionValue = Condition.Evaluate(exchange);

        if (ExpressionResolver.Ast_TryConvertToBool(conditionValue, out var boolResult) && boolResult)
        {
            return IfTrue.Evaluate(exchange);
        }

        return IfFalse.Evaluate(exchange);
    }

    /// <inheritdoc />
    public override string ToString() => $"({Condition} ? {IfTrue} : {IfFalse})";
}
