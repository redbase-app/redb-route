using redb.Route.Abstractions;

namespace redb.Route.Core;

/// <summary>
/// A value that is either static (resolved at endpoint creation)
/// or dynamic (resolved per-message from exchange via ${...} expressions).
/// The property type itself is the contract: DynamicValue{T} = "may be an expression".
/// </summary>
/// <typeparam name="T">The resolved value type.</typeparam>
public readonly struct DynamicValue<T>
{
    private readonly T? _static;
    private readonly Func<IExchange, T?>? _resolver;

    private DynamicValue(T? staticValue, Func<IExchange, T?>? resolver)
    {
        _static = staticValue;
        _resolver = resolver;
    }

    /// <summary>Whether this value contains a dynamic expression.</summary>
    public bool IsDynamic => _resolver != null;

    /// <summary>
    /// Resolves the value. Static = instant return, dynamic = evaluates compiled expression.
    /// </summary>
    /// <param name="exchange">The current exchange (used only for dynamic values).</param>
    /// <returns>Resolved value.</returns>
    public T? Resolve(IExchange exchange) =>
        _resolver != null ? _resolver(exchange) : _static;

    /// <summary>Creates a static (constant) value.</summary>
    /// <param name="value">The constant value.</param>
    /// <returns>A DynamicValue wrapping the constant.</returns>
    public static DynamicValue<T> FromStatic(T value) => new(value, null);

    /// <summary>Creates a dynamic value from a compiled expression resolver.</summary>
    /// <param name="resolver">Compiled expression function, cached after first parse.</param>
    /// <returns>A DynamicValue that evaluates per-message.</returns>
    public static DynamicValue<T> FromExpression(Func<IExchange, T?> resolver) =>
        new(default, resolver ?? throw new ArgumentNullException(nameof(resolver)));

    /// <summary>Creates a dynamic value from an <see cref="IExpression"/>.</summary>
    /// <param name="expression">The expression to evaluate per-message.</param>
    /// <returns>A DynamicValue that evaluates the expression on each resolve.</returns>
    public static DynamicValue<T> FromExpression(IExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return new(default, exchange => expression.Evaluate<T>(exchange));
    }

    /// <summary>Implicit conversion from a plain value to a static DynamicValue.</summary>
    /// <param name="value">The value to wrap.</param>
    public static implicit operator DynamicValue<T>(T value) => FromStatic(value);

    /// <inheritdoc />
    public override string ToString() =>
        IsDynamic ? "${expression}" : _static?.ToString() ?? "null";
}
