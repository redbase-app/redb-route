using System;
using redb.Route.Abstractions;

namespace redb.Route.Expressions;

/// <summary>
/// Expression for accessing message headers in an <see cref="IExchange"/>.
/// </summary>
/// <remarks>
/// Allows extracting message header values
/// for use in predicates and routing logic.
/// </remarks>
public class HeaderExpression : Expression
{
    private readonly string _headerName;

    /// <summary>
    /// Initializes a new instance of the <see cref="HeaderExpression"/> class.
    /// </summary>
    /// <param name="headerName">The header name (case-sensitive).</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="headerName"/> is <c>null</c>.</exception>
    public HeaderExpression(string headerName)
    {
        _headerName = headerName ?? throw new ArgumentNullException(nameof(headerName));
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="exchange"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidCastException">Thrown when the header type is not compatible with <typeparamref name="T"/>.</exception>
    /// <exception cref="KeyNotFoundException">Thrown when the header is not found.</exception>
    public override T Evaluate<T>(IExchange exchange)
    {
        if (exchange == null)
            throw new ArgumentNullException(nameof(exchange));

        return exchange.In.getHeader<T>(_headerName);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="exchange"/> is <c>null</c>.</exception>
    public override void SetValue(IExchange exchange, object value)
    {
        if (exchange == null)
            throw new ArgumentNullException(nameof(exchange));
        exchange.In.setHeader(_headerName, value);
    }

    /// <inheritdoc />
    public override string ToTemplateString() => $"${{header.{_headerName}}}";
}
