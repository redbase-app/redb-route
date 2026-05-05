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
    public override object? Evaluate(IExchange exchange)
    {
        // Get the current value of the operand
        var value = Operand.Evaluate(exchange);
        
        // Get the property name (if the operand is an identifier or property access)
        string propertyName = string.Empty;
        
        if (Operand is IdentifierNode identifierNode)
        {
            propertyName = identifierNode.Name;
        }
        else if (Operand is PropertyAccessNode propertyNode)
        {
            var objName = GetObjectName(propertyNode.Object);
            propertyName = string.IsNullOrEmpty(objName)
                ? propertyNode.PropertyName
                : $"{objName}.{propertyNode.PropertyName}";
        }
        
        if (string.IsNullOrEmpty(propertyName))
        {
            return value; // Cannot apply operation if the property name is unknown
        }
        
        // Save the original value for return (postfix operation behavior)
        var originalValue = value;
        
        // Apply the operation and update the value in the exchange
        if (Operator == "++")
        {
            if (value is int intValue)
            {
                exchange.setProperty(propertyName, intValue + 1);
            }
            else if (value is double doubleValue)
            {
                exchange.setProperty(propertyName, doubleValue + 1);
            }
            else if (value is long longValue)
            {
                exchange.setProperty(propertyName, longValue + 1);
            }
            else if (value is decimal decimalValue)
            {
                exchange.setProperty(propertyName, decimalValue + 1m);
            }
            else if (value == null)
            {
                exchange.setProperty(propertyName, 1);
            }
        }
        else if (Operator == "--")
        {
            if (value is int intValue)
            {
                exchange.setProperty(propertyName, intValue - 1);
            }
            else if (value is double doubleValue)
            {
                exchange.setProperty(propertyName, doubleValue - 1);
            }
            else if (value is long longValue)
            {
                exchange.setProperty(propertyName, longValue - 1);
            }
            else if (value is decimal decimalValue)
            {
                exchange.setProperty(propertyName, decimalValue - 1m);
            }
            else if (value == null)
            {
                exchange.setProperty(propertyName, -1);
            }
        }
        
        // Return the original value (before the change)
        return originalValue;
    }
    
    /// <summary>
    /// Recursively resolves the full dotted object name from a nested AST node.
    /// </summary>
    /// <param name="node">The AST node to resolve the name from.</param>
    /// <returns>The resolved dotted name, or an empty string if the node type is not supported.</returns>
    private string GetObjectName(AstNode node)
    {
        if (node is IdentifierNode idNode)
        {
            return idNode.Name;
        }
        else if (node is PropertyAccessNode propNode)
        {
            var objName = GetObjectName(propNode.Object);
            return string.IsNullOrEmpty(objName)
                ? propNode.PropertyName
                : $"{objName}.{propNode.PropertyName}";
        }
        
        return string.Empty;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"({Operand}){Operator}";
    }
}
