namespace redb.Route.Abstractions;

/// <summary>
/// Represents an expression that can be evaluated against an exchange context.
/// Expressions extract or compute values from a message exchange.
/// Used for content-based routing, transformations, and predicate construction.
/// </summary>
public interface IExpression
{
    /// <summary>
    /// Evaluates the expression and returns the result cast to <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">Target return type.</typeparam>
    /// <param name="exchange">The exchange to evaluate against.</param>
    /// <returns>Evaluation result converted to <typeparamref name="T"/>.</returns>
    T Evaluate<T>(IExchange exchange);

    /// <summary>Creates a predicate that checks equality of this expression's result with the given value.</summary>
    IPredicate isEqualTo(object value);

    /// <summary>Creates a "greater than" predicate.</summary>
    IPredicate isGreaterThan(object value);

    /// <summary>Creates a "less than" predicate.</summary>
    IPredicate isLessThan(object value);

    /// <summary>Creates a "greater than or equal" predicate.</summary>
    IPredicate isGreaterThanOrEqualTo(object value);

    /// <summary>Creates a "less than or equal" predicate.</summary>
    IPredicate isLessThanOrEqualTo(object value);

    /// <summary>Creates a "between" predicate (inclusive).</summary>
    IPredicate isBetween(object low, object high);

    /// <summary>Creates a "contains" predicate (for strings and collections).</summary>
    IPredicate contains(object value);

    /// <summary>Creates a "starts with" predicate (for strings).</summary>
    IPredicate startsWith(object value);

    /// <summary>Creates an "ends with" predicate (for strings).</summary>
    IPredicate endsWith(object value);

    /// <summary>Creates a regex-match predicate.</summary>
    IPredicate regex(string pattern);

    /// <summary>Creates an "in set" predicate.</summary>
    IPredicate In(params object[] values);

    /// <summary>Creates a "not equal" predicate.</summary>
    IPredicate isNotEqualTo(object value);

    /// <summary>Creates an "is null" predicate.</summary>
    IPredicate isNull();

    /// <summary>Creates an "is not null" predicate.</summary>
    IPredicate isNotNull();

    /// <summary>Creates a logical AND predicate combining this expression with another predicate.</summary>
    IPredicate and(IPredicate predicate);

    /// <summary>Creates a logical OR predicate combining this expression with another predicate.</summary>
    IPredicate or(IPredicate predicate);

    /// <summary>Creates a logical NOT predicate negating this expression.</summary>
    IPredicate not();

    /// <summary>
    /// Sets a value in the exchange according to this expression's semantics.
    /// For example, <see cref="redb.Route.Expressions.HeaderExpression"/> sets a header,
    /// <see cref="redb.Route.Expressions.BodyExpression"/> sets the body.
    /// </summary>
    /// <param name="exchange">The target exchange.</param>
    /// <param name="value">The value to set.</param>
    void SetValue(IExchange exchange, object value);

    /// <summary>
    /// Serializes this expression to a <c>${...}</c> template string suitable for URI parameters.
    /// </summary>
    /// <returns>Template string representation, e.g. <c>${header.orderId}</c>.</returns>
    /// <exception cref="System.NotSupportedException">Thrown when the expression cannot be serialized (e.g. delegate-based).</exception>
    string ToTemplateString();
}
