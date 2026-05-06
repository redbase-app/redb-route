using System;
using redb.Route.Abstractions;

namespace redb.Route.Expressions;

/// <summary>
/// Expression for evaluating logical operations such as <c>property.id&gt;0</c>, <c>property.name=="test"</c>, etc.
/// </summary>
public class LogicalExpression : Expression
{
    private readonly string _expression;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogicalExpression"/> class.
    /// </summary>
    /// <param name="expression">The string containing the logical expression.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="expression"/> is <c>null</c>.</exception>
    public LogicalExpression(string expression)
    {
        _expression = expression ?? throw new ArgumentNullException(nameof(expression));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Uses <see cref="ExpressionResolver.EvaluateLogicalExpression"/> to evaluate the logical expression.
    /// On failure, returns <c>false</c> for <see cref="bool"/>, an error message for <see cref="string"/>,
    /// or <c>default</c> for other types.
    /// </remarks>
    public override T Evaluate<T>(IExchange exchange)
    {
        try
        {
            // Use ExpressionResolver to evaluate the logical expression
            bool result = ExpressionResolver.EvaluateLogicalExpression(_expression, exchange);

            // Attempt to convert the result to the requested type
            if (typeof(T) == typeof(bool))
            {
                return (T)(object)result;
            }

            if (typeof(T) == typeof(string))
            {
                return (T)(object)result.ToString();
            }

            if (typeof(T) == typeof(object))
            {
                return (T)(object)result;
            }

            // For other types, try conversion via Convert
            return (T)Convert.ChangeType(result, typeof(T));
        }
        catch (Exception ex)
        {
            throw new ExpressionEvaluationException(
                $"Failed to evaluate logical expression '{_expression}' on exchange {exchange.ExchangeId}",
                ex);
        }
    }

    /// <summary>
    /// Returns the string representation of the logical expression.
    /// </summary>
    /// <returns>The expression string.</returns>
    public override string ToString()
    {
        return _expression;
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always thrown because <see cref="LogicalExpression"/> does not support setting values.</exception>
    public override void SetValue(IExchange exchange, object value)
    {
        throw new NotSupportedException("LogicalExpression does not support setting values.");
    }

    /// <inheritdoc />
    public override string ToTemplateString() => $"${{{_expression}}}";
} 