using redb.Route.Abstractions;
using redb.Route.Expressions;

namespace redb.Route.Templates;

/// <summary>
/// Named arguments of a payload step — the analog of WSO2 PayloadFactory <c>&lt;arg&gt;</c>, but by
/// name instead of position: <c>args.Set("customer", "header.customerId")</c> is read in the template
/// as <c>{{ args.customer }}</c>. Argument expressions are the route expression language and are
/// evaluated on the message <b>before</b> rendering, which keeps the template itself an almost
/// static document a non-programmer can edit.
/// </summary>
public sealed class TemplateArgs
{
    private readonly Dictionary<string, IExpression> _expressions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _constants = new(StringComparer.Ordinal);

    /// <summary>Argument computed from a route-language expression (<c>"header.amount * header.qty"</c>); the expression is compiled now, so a typo fails at <c>Start()</c>.</summary>
    public TemplateArgs Set(string name, string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        return Set(name, new StringExpression(expression));
    }

    /// <summary>Argument computed from an <see cref="IExpression"/>.</summary>
    public TemplateArgs Set(string name, IExpression expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(expression);
        _constants.Remove(name);
        _expressions[name] = expression;
        return this;
    }

    /// <summary>Constant argument (XML form <c>&lt;arg name="channel" value="email"/&gt;</c>).</summary>
    public TemplateArgs SetValue(string name, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _expressions.Remove(name);
        _constants[name] = value;
        return this;
    }

    /// <summary>Argument names, for diagnostics.</summary>
    public IEnumerable<string> Names => _expressions.Keys.Concat(_constants.Keys);

    /// <summary>Evaluates every argument against the exchange.</summary>
    internal Dictionary<string, object?> Evaluate(IExchange exchange)
    {
        var result = new Dictionary<string, object?>(_constants, StringComparer.Ordinal);
        foreach (var (name, expression) in _expressions)
            result[name] = expression.Evaluate<object>(exchange);
        return result;
    }
}
