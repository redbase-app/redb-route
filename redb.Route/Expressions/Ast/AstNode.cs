using System;
using System.Collections.Generic;
using System.Linq;
using redb.Route.Abstractions;

namespace redb.Route.Expressions.Ast;

/// <summary>
/// Abstract base class for all AST (Abstract Syntax Tree) nodes.
/// </summary>
public abstract class AstNode
{
    /// <summary>
    /// Evaluates this AST node against the given exchange context.
    /// </summary>
    /// <param name="exchange">The exchange context used for expression resolution.</param>
    /// <returns>The result of evaluating this node.</returns>
    public abstract object? Evaluate(IExchange exchange);
}

/// <summary>
/// AST node representing a literal (constant) value.
/// </summary>
public class LiteralNode : AstNode
{
    /// <summary>
    /// Gets the literal value held by this node.
    /// </summary>
    public object? Value { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="LiteralNode"/> class.
    /// </summary>
    /// <param name="value">The literal value.</param>
    public LiteralNode(object? value)
    {
        Value = value;
    }

    /// <inheritdoc />
    public override object? Evaluate(IExchange exchange)
    {
        return Value;
    }

    /// <inheritdoc />
    public override string ToString() => Value?.ToString() ?? "null";
}

/// <summary>
/// AST node representing an identifier (variable reference).
/// </summary>
public class IdentifierNode : AstNode
{
    /// <summary>
    /// Gets the name of the identifier.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="IdentifierNode"/> class.
    /// </summary>
    /// <param name="name">The identifier name.</param>
    public IdentifierNode(string name)
    {
        Name = name;
    }

    /// <inheritdoc />
    public override object? Evaluate(IExchange exchange)
    {
        return ExpressionResolver.ResolveExpression(Name, exchange);
    }

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>
/// AST node representing a binary operation (e.g. addition, comparison, logical operations).
/// </summary>
public class BinaryOperationNode : AstNode
{
    /// <summary>
    /// Gets the left operand of the binary operation.
    /// </summary>
    public AstNode Left { get; }

    /// <summary>
    /// Gets the operator symbol (e.g. "+", "==", "AND").
    /// </summary>
    public string Operator { get; }

    /// <summary>
    /// Gets the right operand of the binary operation.
    /// </summary>
    public AstNode Right { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="BinaryOperationNode"/> class.
    /// </summary>
    /// <param name="left">The left operand node.</param>
    /// <param name="op">The operator string.</param>
    /// <param name="right">The right operand node.</param>
    public BinaryOperationNode(AstNode left, string op, AstNode right)
    {
        Left = left;
        Operator = op;
        Right = right;
    }

    /// <inheritdoc />
    public override object? Evaluate(IExchange exchange)
    {
        // Short-circuit: for ??, only evaluate right if left is null
        if (Operator == "??")
        {
            var leftValue = Left.Evaluate(exchange);
            return leftValue ?? Right.Evaluate(exchange);
        }

        var left = Left.Evaluate(exchange);
        var right = Right.Evaluate(exchange);

        switch (Operator)
        {
            case "+": return ExpressionResolver.Ast_ApplyAddition(left, right);
            case "-": return ExpressionResolver.Ast_ApplySubtraction(left, right);
            case "*": return ExpressionResolver.Ast_ApplyMultiplication(left, right);
            case "/": return ExpressionResolver.Ast_ApplyDivision(left, right);
            case "==": return ExpressionResolver.Ast_AreEqual(left, right);
            case "!=": return !ExpressionResolver.Ast_AreEqual(left, right);
            case ">": return ExpressionResolver.Ast_CompareNumeric(left, right) > 0;
            case "<": return ExpressionResolver.Ast_CompareNumeric(left, right) < 0;
            case ">=": return ExpressionResolver.Ast_CompareNumeric(left, right) >= 0;
            case "<=": return ExpressionResolver.Ast_CompareNumeric(left, right) <= 0;
            case "AND": return EvaluateAnd(left, right);
            case "OR": return EvaluateOr(left, right);
            case "XOR": return EvaluateXor(left, right);
            default: throw new NotSupportedException($"Unsupported operator: {Operator}");
        }
    }

    /// <summary>
    /// Evaluates logical AND between two values.
    /// </summary>
    /// <param name="left">The left operand value.</param>
    /// <param name="right">The right operand value.</param>
    /// <returns><c>true</c> if both operands are truthy; otherwise <c>false</c>.</returns>
    private bool EvaluateAnd(object? left, object? right)
    {
        if (ExpressionResolver.Ast_TryConvertToBool(left, out var leftBool) && 
            ExpressionResolver.Ast_TryConvertToBool(right, out var rightBool))
        {
            return leftBool && rightBool;
        }
        return false;
    }

    /// <summary>
    /// Evaluates logical OR between two values.
    /// </summary>
    /// <param name="left">The left operand value.</param>
    /// <param name="right">The right operand value.</param>
    /// <returns><c>true</c> if at least one operand is truthy; otherwise <c>false</c>.</returns>
    private bool EvaluateOr(object? left, object? right)
    {
        if (ExpressionResolver.Ast_TryConvertToBool(left, out var leftBool) && 
            ExpressionResolver.Ast_TryConvertToBool(right, out var rightBool))
        {
            return leftBool || rightBool;
        }
        return false;
    }

    /// <summary>
    /// Evaluates logical XOR between two values.
    /// </summary>
    /// <param name="left">The left operand value.</param>
    /// <param name="right">The right operand value.</param>
    /// <returns><c>true</c> if exactly one operand is truthy; otherwise <c>false</c>.</returns>
    private bool EvaluateXor(object? left, object? right)
    {
        if (ExpressionResolver.Ast_TryConvertToBool(left, out var leftBool) && 
            ExpressionResolver.Ast_TryConvertToBool(right, out var rightBool))
        {
            return leftBool ^ rightBool;
        }
        return false;
    }

    /// <inheritdoc />
    public override string ToString() => $"({Left} {Operator} {Right})";
}

/// <summary>
/// AST node representing a unary (prefix) operation (e.g. negation, logical NOT, prefix increment/decrement).
/// </summary>
public class UnaryOperationNode : AstNode
{
    /// <summary>
    /// Gets the unary operator symbol (e.g. "+", "-", "!", "NOT", "++", "--").
    /// </summary>
    public string Operator { get; }

    /// <summary>
    /// Gets the operand of the unary operation.
    /// </summary>
    public AstNode Operand { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="UnaryOperationNode"/> class.
    /// </summary>
    /// <param name="op">The unary operator string.</param>
    /// <param name="operand">The operand node.</param>
    public UnaryOperationNode(string op, AstNode operand)
    {
        Operator = op;
        Operand = operand;
    }

    /// <inheritdoc />
    public override object? Evaluate(IExchange exchange)
    {
        // Handle ++ and -- prefix operations specially
        if (Operator == "++" || Operator == "--")
        {
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
            
            if (!string.IsNullOrEmpty(propertyName))
            {
                // Get the current property value
                var currentValue = Operand.Evaluate(exchange);
                
                // Apply the increment/decrement operation
                if (Operator == "++")
                {
                    return ExpressionResolver.Ast_ApplyPrefixIncrement(currentValue, propertyName, exchange);
                }
                else // Operator == "--"
                {
                    return ExpressionResolver.Ast_ApplyPrefixDecrement(currentValue, propertyName, exchange);
                }
            }
        }
        
        // Get the operand value for other unary operations
        var operandValue = Operand.Evaluate(exchange);

        switch (Operator)
        {
            case "+": return ExpressionResolver.Ast_ApplyUnaryPlus(operandValue);
            case "-": return ExpressionResolver.Ast_ApplyUnaryMinus(operandValue);
            case "NOT":
            case "!": return ExpressionResolver.Ast_ApplyUnaryNot(operandValue);
            default: throw new NotSupportedException($"Unsupported unary operator: {Operator}");
        }
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
    public override string ToString() => $"{Operator}({Operand})";
}

/// <summary>
/// AST node representing a function call with arguments.
/// </summary>
public class FunctionCallNode : AstNode
{
    /// <summary>
    /// Gets the name of the function being called.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the list of argument nodes passed to the function.
    /// </summary>
    public List<AstNode> Arguments { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="FunctionCallNode"/> class.
    /// </summary>
    /// <param name="name">The function name.</param>
    /// <param name="arguments">The list of argument AST nodes.</param>
    public FunctionCallNode(string name, List<AstNode> arguments)
    {
        Name = name;
        Arguments = arguments;
    }

    /// <inheritdoc />
    public override object? Evaluate(IExchange exchange)
    {
        // Evaluate argument values
        var evaluatedArgs = Arguments.Select(arg => arg.Evaluate(exchange)).ToArray();

        switch (Name.ToLowerInvariant())
        {
            case "logical":
                if (evaluatedArgs.Length == 1 && 
                    ExpressionResolver.TryConvertToBool(evaluatedArgs[0], out var result))
                {
                    return result;
                }
                return false;

            case "jpath":
                if (evaluatedArgs.Length == 1)
                {
                    var pathValue = evaluatedArgs[0]?.ToString();
                    if (pathValue != null)
                    {
                        var jPathExpr = new JsonPathExpression(pathValue);
                        return jPathExpr.Evaluate<object>(exchange);
                    }
                }
                return null;

            case "xpath":
                if (evaluatedArgs.Length == 1)
                {
                    var xpathValue = evaluatedArgs[0]?.ToString();
                    if (xpathValue != null)
                    {
                        var xPathExpr = new XPathExpression(xpathValue);
                        return xPathExpr.Evaluate<object>(exchange);
                    }
                }
                return null;

            case "concat":
                return string.Concat(evaluatedArgs.Select(a => a?.ToString() ?? string.Empty));

            case "upper":
                return evaluatedArgs.Length >= 1 ? evaluatedArgs[0]?.ToString()?.ToUpperInvariant() : null;

            case "lower":
                return evaluatedArgs.Length >= 1 ? evaluatedArgs[0]?.ToString()?.ToLowerInvariant() : null;

            case "trim":
                return evaluatedArgs.Length >= 1 ? evaluatedArgs[0]?.ToString()?.Trim() : null;

            case "length":
                if (evaluatedArgs.Length >= 1)
                {
                    if (evaluatedArgs[0] == null) return null;
                    if (evaluatedArgs[0] is string s) return s.Length;
                    if (evaluatedArgs[0] is System.Collections.ICollection col) return col.Count;
                    if (evaluatedArgs[0] is System.Collections.IEnumerable en)
                        return System.Linq.Enumerable.Count(System.Linq.Enumerable.Cast<object>(en));
                    return evaluatedArgs[0].ToString()?.Length;
                }
                return null;

            case "substring":
                if (evaluatedArgs.Length >= 2)
                {
                    var str = evaluatedArgs[0]?.ToString();
                    if (str == null) return null;
                    if (ExpressionResolver.TryConvertToDouble(evaluatedArgs[1], out var startIdx))
                    {
                        var start = (int)startIdx;
                        if (start < 0) start = 0;
                        if (start >= str.Length) return string.Empty;
                        if (evaluatedArgs.Length >= 3 && ExpressionResolver.TryConvertToDouble(evaluatedArgs[2], out var lenVal))
                        {
                            var len = (int)lenVal;
                            if (len <= 0) return string.Empty;
                            return str.Substring(start, Math.Min(len, str.Length - start));
                        }
                        return str.Substring(start);
                    }
                }
                return evaluatedArgs.Length >= 1 ? evaluatedArgs[0]?.ToString() : null;

            case "abs":
                if (evaluatedArgs.Length >= 1 && ExpressionResolver.TryConvertToDouble(evaluatedArgs[0], out var absVal))
                    return absVal == (int)absVal ? (object)(int)Math.Abs(absVal) : Math.Abs(absVal);
                return null;

            case "round":
                if (evaluatedArgs.Length >= 1 && ExpressionResolver.TryConvertToDouble(evaluatedArgs[0], out var roundVal))
                {
                    int digits = 0;
                    if (evaluatedArgs.Length >= 2 && ExpressionResolver.TryConvertToDouble(evaluatedArgs[1], out var d))
                        digits = (int)d;
                    return Math.Round(roundVal, digits);
                }
                return null;

            case "min":
                if (evaluatedArgs.Length >= 2
                    && ExpressionResolver.TryConvertToDouble(evaluatedArgs[0], out var minA)
                    && ExpressionResolver.TryConvertToDouble(evaluatedArgs[1], out var minB))
                    return Math.Min(minA, minB);
                return null;

            case "max":
                if (evaluatedArgs.Length >= 2
                    && ExpressionResolver.TryConvertToDouble(evaluatedArgs[0], out var maxA)
                    && ExpressionResolver.TryConvertToDouble(evaluatedArgs[1], out var maxB))
                    return Math.Max(maxA, maxB);
                return null;

            case "contains":
                if (evaluatedArgs.Length >= 2)
                {
                    var cStr = evaluatedArgs[0]?.ToString();
                    var cSearch = evaluatedArgs[1]?.ToString();
                    if (cStr == null || cSearch == null) return false;
                    return cStr.Contains(cSearch, StringComparison.OrdinalIgnoreCase);
                }
                return false;

            case "startswith":
                if (evaluatedArgs.Length >= 2)
                {
                    var swStr = evaluatedArgs[0]?.ToString();
                    var swPrefix = evaluatedArgs[1]?.ToString();
                    if (swStr == null || swPrefix == null) return false;
                    return swStr.StartsWith(swPrefix, StringComparison.OrdinalIgnoreCase);
                }
                return false;

            case "endswith":
                if (evaluatedArgs.Length >= 2)
                {
                    var ewStr = evaluatedArgs[0]?.ToString();
                    var ewSuffix = evaluatedArgs[1]?.ToString();
                    if (ewStr == null || ewSuffix == null) return false;
                    return ewStr.EndsWith(ewSuffix, StringComparison.OrdinalIgnoreCase);
                }
                return false;

            case "replace":
                if (evaluatedArgs.Length >= 3)
                {
                    var rStr = evaluatedArgs[0]?.ToString();
                    var rOld = evaluatedArgs[1]?.ToString();
                    var rNew = evaluatedArgs[2]?.ToString() ?? string.Empty;
                    if (rStr == null || rOld == null) return rStr;
                    return rStr.Replace(rOld, rNew);
                }
                return evaluatedArgs.Length >= 1 ? evaluatedArgs[0]?.ToString() : null;

            case "now":
                return DateTime.UtcNow;

            case "dateformat":
                if (evaluatedArgs.Length >= 2)
                {
                    var dfFormat = evaluatedArgs[1]?.ToString() ?? "yyyy-MM-dd";
                    if (evaluatedArgs[0] is DateTime dt)
                        return dt.ToString(dfFormat, System.Globalization.CultureInfo.InvariantCulture);
                    if (evaluatedArgs[0] is DateTimeOffset dto)
                        return dto.ToString(dfFormat, System.Globalization.CultureInfo.InvariantCulture);
                    if (DateTime.TryParse(evaluatedArgs[0]?.ToString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed))
                        return parsed.ToString(dfFormat, System.Globalization.CultureInfo.InvariantCulture);
                }
                return evaluatedArgs.Length >= 1 ? evaluatedArgs[0]?.ToString() : null;

            case "dateadd":
                if (evaluatedArgs.Length >= 3)
                {
                    DateTime baseDate;
                    if (evaluatedArgs[0] is DateTime d1) baseDate = d1;
                    else if (evaluatedArgs[0] is DateTimeOffset d2) baseDate = d2.UtcDateTime;
                    else if (DateTime.TryParse(evaluatedArgs[0]?.ToString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var p)) baseDate = p;
                    else return null;

                    if (!ExpressionResolver.TryConvertToDouble(evaluatedArgs[1], out var amount)) return null;
                    var unit = evaluatedArgs[2]?.ToString()?.ToLowerInvariant() ?? "days";
                    return unit switch
                    {
                        "days" or "day" => baseDate.AddDays(amount),
                        "hours" or "hour" => baseDate.AddHours(amount),
                        "minutes" or "minute" => baseDate.AddMinutes(amount),
                        "seconds" or "second" => baseDate.AddSeconds(amount),
                        "months" or "month" => baseDate.AddMonths((int)amount),
                        "years" or "year" => baseDate.AddYears((int)amount),
                        _ => null
                    };
                }
                return null;

            case "sum":
                if (evaluatedArgs.Length >= 1 && evaluatedArgs[0] is System.Collections.IEnumerable sumEnum)
                {
                    double sumTotal = 0;
                    foreach (var item in sumEnum)
                        if (ExpressionResolver.TryConvertToDouble(item, out var sv)) sumTotal += sv;
                    return sumTotal;
                }
                return null;

            case "avg":
                if (evaluatedArgs.Length >= 1 && evaluatedArgs[0] is System.Collections.IEnumerable avgEnum)
                {
                    double avgTotal = 0; int avgCount = 0;
                    foreach (var item in avgEnum)
                        if (ExpressionResolver.TryConvertToDouble(item, out var av)) { avgTotal += av; avgCount++; }
                    return avgCount > 0 ? avgTotal / avgCount : (object?)null;
                }
                return null;

            case "count":
                if (evaluatedArgs.Length >= 1)
                {
                    if (evaluatedArgs[0] == null) return 0;
                    if (evaluatedArgs[0] is System.Collections.ICollection countCol) return countCol.Count;
                    if (evaluatedArgs[0] is System.Collections.IEnumerable countEnum)
                        return System.Linq.Enumerable.Count(System.Linq.Enumerable.Cast<object>(countEnum));
                    return 1;
                }
                return 0;

            default:
                throw new NotSupportedException($"Unsupported function: {Name}");
        }
    }

    /// <inheritdoc />
    public override string ToString() => $"{Name}({string.Join(", ", Arguments)})";
}

/// <summary>
/// AST node representing property access on an object (e.g. <c>obj.PropertyName</c>).
/// </summary>
public class PropertyAccessNode : AstNode
{
    /// <summary>
    /// Gets the AST node representing the object being accessed.
    /// </summary>
    public AstNode Object { get; }

    /// <summary>
    /// Gets the name of the property being accessed.
    /// </summary>
    public string PropertyName { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PropertyAccessNode"/> class.
    /// </summary>
    /// <param name="obj">The object node whose property is accessed.</param>
    /// <param name="propertyName">The name of the property to access.</param>
    public PropertyAccessNode(AstNode obj, string propertyName)
    {
        Object = obj;
        PropertyName = propertyName;
    }

    /// <inheritdoc />
    public override object? Evaluate(IExchange exchange)
    {
        var objValue = Object.Evaluate(exchange);
        if (objValue == null) return null;

        return ExpressionResolver.Ast_ResolvePropertyPath(objValue, PropertyName);
    }

    /// <inheritdoc />
    public override string ToString() => $"{Object}.{PropertyName}";
}
