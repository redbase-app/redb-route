using System.Collections;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Expressions;

namespace redb.Route.Processors;

/// <summary>
/// Resolves a string template: single <c>${expr}</c> preserves CLR type,
/// composite templates (with surrounding text) return string.
/// </summary>
internal static class ExpressionHelper
{
    internal static object? ResolveTypedOrTemplate(string template, IExchange exchange)
        => ExpressionResolver.ResolveTypedOrTemplate(template, exchange);
}

/// <summary>
/// Processor that evaluates an <see cref="IExpression"/> and sets the result as the exchange body.
/// Used by the DSL <c>.SetBody(IExpression)</c> and <c>.Transform(IExpression)</c>.
/// </summary>
public sealed class ExpressionBodyProcessor : IProcessor
{
    private readonly IExpression _expression;

    /// <summary>Creates a new instance with the specified expression.</summary>
    /// <param name="expression">Expression producing the new body value.</param>
    public ExpressionBodyProcessor(IExpression expression)
    {
        _expression = expression ?? throw new ArgumentNullException(nameof(expression));
    }

    /// <inheritdoc />
    public Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var result = _expression.Evaluate<object>(exchange);
        exchange.In.Body = result;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Processor that resolves a string expression template via <see cref="ExpressionResolver"/>
/// and sets the result as the exchange body.
/// Used by <c>.SetBodyExpression(string)</c> and <c>.TransformExpression(string)</c>.
/// </summary>
public sealed class StringExpressionBodyProcessor : IProcessor
{
    private readonly string _template;

    /// <summary>Creates a new instance with the specified template.</summary>
    /// <param name="template">Expression template with <c>${...}</c> placeholders.</param>
    public StringExpressionBodyProcessor(string template)
    {
        _template = template ?? throw new ArgumentNullException(nameof(template));
    }

    /// <inheritdoc />
    public Task Process(IExchange exchange, CancellationToken ct = default)
    {
        exchange.In.Body = ExpressionHelper.ResolveTypedOrTemplate(_template, exchange);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Processor that evaluates an <see cref="IExpression"/> and sets the result as a header.
/// Used by <c>.SetHeader(key, IExpression)</c>.
/// </summary>
public sealed class ExpressionHeaderProcessor : IProcessor
{
    private readonly string _headerName;
    private readonly IExpression _expression;

    /// <summary>Creates a new instance.</summary>
    /// <param name="headerName">Header key to set.</param>
    /// <param name="expression">Expression producing the header value.</param>
    public ExpressionHeaderProcessor(string headerName, IExpression expression)
    {
        _headerName = headerName ?? throw new ArgumentNullException(nameof(headerName));
        _expression = expression ?? throw new ArgumentNullException(nameof(expression));
    }

    /// <inheritdoc />
    public Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var result = _expression.Evaluate<object>(exchange);
        exchange.In.Headers[_headerName] = result;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Processor that evaluates an <see cref="IExpression"/> and sets the result as a property.
/// Used by <c>.SetProperty(key, IExpression)</c>.
/// </summary>
public sealed class ExpressionPropertyProcessor : IProcessor
{
    private readonly string _propertyName;
    private readonly IExpression _expression;

    /// <summary>Creates a new instance.</summary>
    /// <param name="propertyName">Property key to set.</param>
    /// <param name="expression">Expression producing the property value.</param>
    public ExpressionPropertyProcessor(string propertyName, IExpression expression)
    {
        _propertyName = propertyName ?? throw new ArgumentNullException(nameof(propertyName));
        _expression = expression ?? throw new ArgumentNullException(nameof(expression));
    }

    /// <inheritdoc />
    public Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var result = _expression.Evaluate<object>(exchange);
        exchange.Properties[_propertyName] = result;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Processor that resolves a string expression template and sets the result as a header.
/// Used by <c>.SetHeaderExpression(key, string)</c>.
/// </summary>
public sealed class StringExpressionHeaderProcessor : IProcessor
{
    private readonly string _headerName;
    private readonly string _template;

    /// <summary>Creates a new instance.</summary>
    /// <param name="headerName">Header key to set.</param>
    /// <param name="template">Expression template with <c>${...}</c> placeholders.</param>
    public StringExpressionHeaderProcessor(string headerName, string template)
    {
        _headerName = headerName ?? throw new ArgumentNullException(nameof(headerName));
        _template = template ?? throw new ArgumentNullException(nameof(template));
    }

    /// <inheritdoc />
    public Task Process(IExchange exchange, CancellationToken ct = default)
    {
        exchange.In.Headers[_headerName] = ExpressionHelper.ResolveTypedOrTemplate(_template, exchange);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Processor that resolves a string expression template and sets the result as an exchange property.
/// Used by <c>.SetPropertyExpression(key, string)</c>.
/// </summary>
public sealed class StringExpressionPropertyProcessor : IProcessor
{
    private readonly string _propertyName;
    private readonly string _template;

    /// <summary>Creates a new instance.</summary>
    /// <param name="propertyName">Property key to set.</param>
    /// <param name="template">Expression template with <c>${...}</c> placeholders.</param>
    public StringExpressionPropertyProcessor(string propertyName, string template)
    {
        _propertyName = propertyName ?? throw new ArgumentNullException(nameof(propertyName));
        _template = template ?? throw new ArgumentNullException(nameof(template));
    }

    /// <inheritdoc />
    public Task Process(IExchange exchange, CancellationToken ct = default)
    {
        exchange.Properties[_propertyName] = ExpressionHelper.ResolveTypedOrTemplate(_template, exchange);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Processor that logs a message using ExpressionResolver template syntax.
/// Supports <c>${body}</c>, <c>${header.name}</c>, <c>${property.name}</c> placeholders.
/// Used by <c>.Log(string)</c> when the message contains <c>${...}</c> placeholders.
/// </summary>
public sealed class TemplateLogProcessor : IProcessor
{
    private readonly string _template;
    private readonly LogLevel _level;
    private readonly ILogger? _logger;

    /// <summary>Creates a new instance.</summary>
    /// <param name="template">Template string with <c>${...}</c> placeholders.</param>
    /// <param name="level">Log level.</param>
    /// <param name="logger">Logger instance (null = NullLogger).</param>
    public TemplateLogProcessor(string template, LogLevel level, ILogger? logger = null)
    {
        _template = template ?? throw new ArgumentNullException(nameof(template));
        _level = level;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task Process(IExchange exchange, CancellationToken ct = default)
    {
        if (_logger is not null && _logger.IsEnabled(_level))
        {
            var resolved = ExpressionResolver.ProcessTemplate(_template, exchange);
            _logger.Log(_level, "{Message}", resolved);
        }
        return Task.CompletedTask;
    }
}
