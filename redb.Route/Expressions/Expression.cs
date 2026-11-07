using redb.Route.Abstractions;
using redb.Route.Predicates;

namespace redb.Route.Expressions;

/// <summary>
/// Abstract base class for expressions that implements the <see cref="IExpression"/> interface.
/// Provides a standard set of predicate factory methods for building routing conditions.
/// </summary>
public abstract class Expression : IExpression
{
    /// <inheritdoc />
    public abstract T Evaluate<T>(IExchange exchange);

    /// <inheritdoc />
    public abstract void SetValue(IExchange exchange, object value);

    /// <summary>Creates a predicate that checks whether the expression value equals the specified value.</summary>
    /// <param name="value">The value to compare against.</param>
    /// <returns>An <see cref="IPredicate"/> representing the equality check.</returns>
    public IPredicate isEqualTo(object value) => new EqualsPredicate(this, value);

    /// <summary>Creates a predicate that checks whether the expression value is greater than the specified value.</summary>
    /// <param name="value">The value to compare against.</param>
    /// <returns>An <see cref="IPredicate"/> representing the greater-than check.</returns>
    public IPredicate isGreaterThan(object value) => new GreaterThanPredicate(this, value);

    /// <summary>Creates a predicate that checks whether the expression value is less than the specified value.</summary>
    /// <param name="value">The value to compare against.</param>
    /// <returns>An <see cref="IPredicate"/> representing the less-than check.</returns>
    public IPredicate isLessThan(object value) => new LessThanPredicate(this, value);

    /// <summary>Creates a predicate that checks whether the expression value is greater than or equal to the specified value.</summary>
    /// <param name="value">The value to compare against.</param>
    /// <returns>An <see cref="IPredicate"/> representing the greater-than-or-equal check.</returns>
    public IPredicate isGreaterThanOrEqualTo(object value) => new GreaterThanOrEqualPredicate(this, value);

    /// <summary>Creates a predicate that checks whether the expression value is less than or equal to the specified value.</summary>
    /// <param name="value">The value to compare against.</param>
    /// <returns>An <see cref="IPredicate"/> representing the less-than-or-equal check.</returns>
    public IPredicate isLessThanOrEqualTo(object value) => new LessThanOrEqualPredicate(this, value);

    /// <summary>Creates a predicate that checks whether the expression value falls between two boundaries (inclusive).</summary>
    /// <param name="low">The lower boundary.</param>
    /// <param name="high">The upper boundary.</param>
    /// <returns>An <see cref="IPredicate"/> representing the between check.</returns>
    public IPredicate isBetween(object low, object high) => new BetweenPredicate(this, low, high);

    /// <summary>Creates a predicate that checks whether the expression value contains the specified value (for strings/collections).</summary>
    /// <param name="value">The value to search for.</param>
    /// <returns>An <see cref="IPredicate"/> representing the contains check.</returns>
    public IPredicate contains(object value) => new ContainsPredicate(this, value);

    /// <summary>Creates a predicate that checks whether the expression value starts with the specified value.</summary>
    /// <param name="value">The prefix value to check.</param>
    /// <returns>An <see cref="IPredicate"/> representing the starts-with check.</returns>
    public IPredicate startsWith(object value) => new StartsWithPredicate(this, value);

    /// <summary>Creates a predicate that checks whether the expression value ends with the specified value.</summary>
    /// <param name="value">The suffix value to check.</param>
    /// <returns>An <see cref="IPredicate"/> representing the ends-with check.</returns>
    public IPredicate endsWith(object value) => new EndsWithPredicate(this, value);

    /// <summary>Creates a predicate that checks whether the expression value matches the specified regular expression pattern.</summary>
    /// <param name="pattern">The regular expression pattern.</param>
    /// <returns>An <see cref="IPredicate"/> representing the regex match check.</returns>
    public IPredicate regex(string pattern) => new RegexPredicate(this, pattern);

    /// <summary>Creates a predicate that checks whether the expression value is contained in the specified set of values.</summary>
    /// <param name="values">The set of values to check against.</param>
    /// <returns>An <see cref="IPredicate"/> representing the membership check.</returns>
    public IPredicate In(params object[] values) => new InPredicate(this, values);

    /// <summary>Creates a predicate that checks whether the expression value does not equal the specified value.</summary>
    /// <param name="value">The value to compare against.</param>
    /// <returns>An <see cref="IPredicate"/> representing the inequality check.</returns>
    public IPredicate isNotEqualTo(object value) => new NotEqualsPredicate(this, value);

    /// <summary>Creates a predicate that checks whether the expression value is <c>null</c>.</summary>
    /// <returns>An <see cref="IPredicate"/> representing the null check.</returns>
    public IPredicate isNull() => new IsNullPredicate(this);

    /// <summary>Creates a predicate that checks whether the expression value is not <c>null</c>.</summary>
    /// <returns>An <see cref="IPredicate"/> representing the not-null check.</returns>
    public IPredicate isNotNull() => new IsNotNullPredicate(this);

    /// <summary>Creates a predicate that combines the current condition with another using logical AND.</summary>
    /// <param name="predicate">The predicate to combine with.</param>
    /// <returns>An <see cref="IPredicate"/> representing the AND combination.</returns>
    public IPredicate and(IPredicate predicate) => new AndPredicate(this, predicate);

    /// <summary>Creates a predicate that combines the current condition with another using logical OR.</summary>
    /// <param name="predicate">The predicate to combine with.</param>
    /// <returns>An <see cref="IPredicate"/> representing the OR combination.</returns>
    public IPredicate or(IPredicate predicate) => new OrPredicate(this, predicate);

    /// <summary>Creates a predicate that negates the current condition.</summary>
    /// <returns>An <see cref="IPredicate"/> representing the negated condition.</returns>
    public IPredicate not() => new NotPredicate(this);

    /// <inheritdoc />
    public virtual string ToTemplateString() =>
        throw new NotSupportedException($"{GetType().Name} cannot be serialized to a template string.");
}
