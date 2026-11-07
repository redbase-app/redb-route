using System;

namespace redb.Route.Expressions;

/// <summary>
/// Thrown when an expression fails to evaluate against an exchange.
/// </summary>
public sealed class ExpressionEvaluationException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ExpressionEvaluationException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception that caused this failure.</param>
    public ExpressionEvaluationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ExpressionEvaluationException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    public ExpressionEvaluationException(string message)
        : base(message)
    {
    }
}
