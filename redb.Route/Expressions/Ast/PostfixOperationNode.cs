using System;
using redb.Route.Abstractions;

namespace redb.Route.Expressions.Ast;

/// <summary>
/// AST node for postfix operations (e.g. <c>x++</c>, <c>x--</c>).
/// Returns the original value before the increment/decrement is applied.
/// </summary>
public class PostfixOperationNode : AstNode
{
    /// <summary>
    /// Gets the operand (the expression being incremented or decremented).
    /// </summary>
    public AstNode Operand { get; }

    /// <summary>
    /// Gets the postfix operator ("++" or "--").
    /// </summary>
    public string Operator { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PostfixOperationNode"/> class.
    /// </summary>
    /// <param name="operand">The operand node.</param>
    /// <param name="operator">The postfix operator string ("++" or "--").</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="operand"/> is <c>null</c>.</exception>
    public PostfixOperationNode(AstNode operand, string @operator)
    {
        Operand = operand ?? throw new ArgumentNullException(nameof(operand));
        Operator = @operator;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"({Operand}){Operator}";
    }
}
