using redb.Route.Abstractions;

namespace redb.Route.Expressions.Ast;

/// <summary>
/// AST node representing index access on a collection/array (e.g. <c>items[0]</c>).
/// </summary>
public class IndexAccessNode : AstNode
{
    /// <summary>
    /// Gets the AST node representing the object being indexed.
    /// </summary>
    public AstNode Object { get; }

    /// <summary>
    /// Gets the AST node representing the index expression.
    /// </summary>
    public AstNode Index { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="IndexAccessNode"/> class.
    /// </summary>
    /// <param name="obj">The object node being indexed.</param>
    /// <param name="index">The index expression node.</param>
    public IndexAccessNode(AstNode obj, AstNode index)
    {
        Object = obj;
        Index = index;
    }

    /// <inheritdoc />
    public override object? Evaluate(IExchange exchange)
    {
        var objValue = Object.Evaluate(exchange);
        var indexValue = Index.Evaluate(exchange);

        return ExpressionResolver.Ast_IndexAccess(objValue, indexValue);
    }

    /// <inheritdoc />
    public override string ToString() => $"{Object}[{Index}]";
}
