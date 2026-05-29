using System.Text.RegularExpressions;
using redb.Route.Abstractions;

namespace redb.Route.Expressions;

/// <summary>
/// Expression that evaluates a string expression template via <see cref="ExpressionResolver"/>.
/// Supports <c>${...}</c> template interpolation and raw expression strings.
/// <para>
/// Unifies the two expression worlds: typed <see cref="IExpression"/> objects and string-based
/// <c>${...}</c> templates. Inherits all predicate methods from <see cref="Expression"/>
/// (isEqualTo, contains, isGreaterThan, etc.), so string expressions can be used anywhere
/// an <see cref="IExpression"/> is expected.
/// </para>
/// <example>
/// <code>
/// // In RouteBuilder:
/// .SetBody(Expr("${header.source}"))
/// .Filter(Expr("${header.amount}").isGreaterThan(1000))
/// .SetHeader("full", Expr("${header.first} ${header.last}"))
/// </code>
/// </example>
/// </summary>
public sealed class StringExpression : Expression
{
    private static readonly Regex TemplatePattern = new(@"\$\{([^}]+)\}", RegexOptions.Compiled);

    private readonly string _template;
    private readonly bool _isTemplate;
    private readonly Func<IExchange, object?>? _compiledValue;
    private readonly Func<IExchange, string>? _compiledTemplate;

    /// <summary>
    /// Initializes a new instance with the specified expression template.
    /// </summary>
    /// <param name="template">
    /// Expression string. Can be a <c>${...}</c> template (e.g. <c>"${header.x} world"</c>)
    /// or a raw expression (e.g. <c>"header.amount + 100"</c>).
    /// </param>
    public StringExpression(string template)
    {
        ArgumentException.ThrowIfNullOrEmpty(template, nameof(template));
        _template = template;

        // Determine mode: template interpolation (contains ${...}) vs raw value expression
        _isTemplate = TemplatePattern.IsMatch(template);

        if (_isTemplate)
        {
            _compiledTemplate = ExpressionResolver.GetCompiledTemplate(template);
        }
        else
        {
            _compiledValue = ExpressionResolver.GetCompiledValueExpression(template);
        }
    }

    /// <summary>Gets the original expression template string.</summary>
    public string Template => _template;

    /// <inheritdoc />
    public override T Evaluate<T>(IExchange exchange)
    {
        ArgumentNullException.ThrowIfNull(exchange, nameof(exchange));

        object? result;

        if (_isTemplate)
        {
            // "${header.x} world" → string interpolation
            result = _compiledTemplate!(exchange);
        }
        else
        {
            // "header.amount + 100" → value expression
            result = _compiledValue!(exchange);
        }

        if (result is null)
            return default!;

        if (result is T typed)
            return typed;

        return (T)Convert.ChangeType(result, typeof(T));
    }

    /// <summary>
    /// String expressions are read-only — they compute a value but have no target to write to.
    /// </summary>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public override void SetValue(IExchange exchange, object value)
        => throw new NotSupportedException(
            $"StringExpression '{_template}' is read-only. " +
            "Use Body(), Header(name), or Property(name) for settable expressions.");

    /// <inheritdoc />
    public override string ToString() => $"Expr(\"{_template}\")";

    /// <inheritdoc />
    public override string ToTemplateString() => _template;
}
