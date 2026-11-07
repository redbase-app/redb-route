using redb.Route.Abstractions;

namespace redb.Route.Sql;

/// <summary>
/// The parameters of one statement, worked out once: its distinct <c>:#name</c> placeholders, the explicit <c>param.name</c>
/// value each uses, the text in the provider's placeholder style and the parameters that text takes. A batch builds the plan
/// once and binds every item with it.
/// </summary>
internal sealed class SqlParameterPlan
{
    private readonly Dictionary<string, ExplicitSqlParameter> _explicit;

    private SqlParameterPlan(
        string sourceSql, string sql, IReadOnlyList<string> names, IReadOnlyList<int> slots,
        Dictionary<string, ExplicitSqlParameter> explicitParameters)
    {
        SourceSql = sourceSql;
        Sql = sql;
        Names = names;
        Slots = slots;
        _explicit = explicitParameters;
        HasExpressions = explicitParameters.Values.Any(p => p.IsExpression);
    }

    /// <summary>The statement as written, with <c>:#name</c> placeholders.</summary>
    internal string SourceSql { get; }

    /// <summary>The statement sent to the provider: placeholders in its style, everything else as written.</summary>
    internal string Sql { get; }

    /// <summary>Distinct placeholder names, without <c>:#</c>, in the order they first appear.</summary>
    internal IReadOnlyList<string> Names { get; }

    /// <summary>
    /// The parameters of <see cref="Sql"/>, in order: parameter <c>i</c> carries the value of <c>Names[Slots[i]]</c>. One per
    /// name for <see cref="SqlPlaceholderStyle.At"/>; one per occurrence for the positional styles.
    /// </summary>
    internal IReadOnlyList<int> Slots { get; }

    /// <summary>At least one explicit value is a <c>${...}</c> expression, so binding needs an exchange to evaluate it in.</summary>
    internal bool HasExpressions { get; }

    /// <summary>
    /// Parses <paramref name="sql"/> (with <paramref name="backslashEscapes"/>, as MySQL reads its literals), picks the explicit
    /// values its placeholders use and writes it in <paramref name="style"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">A <c>:#</c> placeholder form that is not supported.</exception>
    internal static SqlParameterPlan Create(
        string sql, IReadOnlyDictionary<string, string> explicitParameters, SqlPlaceholderStyle style = SqlPlaceholderStyle.At,
        bool backslashEscapes = false)
    {
        var placeholders = SqlParameterParser.FindPlaceholders(sql, backslashEscapes);
        var names = new List<string>();
        var indexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var occurrences = new List<int>(placeholders.Count);
        // Names are compared ignoring case, and the text sent to the provider spells every occurrence the way the name was
        // first written: a provider that binds names case-sensitively (SQLite) must see one name for the one parameter.
        var canonical = new SqlPlaceholder[placeholders.Count];
        for (var i = 0; i < placeholders.Count; i++)
        {
            var placeholder = placeholders[i];
            if (!indexByName.TryGetValue(placeholder.Name, out var index))
            {
                index = names.Count;
                indexByName[placeholder.Name] = index;
                names.Add(placeholder.Name);
            }

            occurrences.Add(index);
            canonical[i] = placeholder with { Name = names[index] };
        }

        var used = new Dictionary<string, ExplicitSqlParameter>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (explicitParameters.TryGetValue(name, out var value))
                used[name] = new ExplicitSqlParameter(value);
        }

        IReadOnlyList<int> slots = style == SqlPlaceholderStyle.At ? Enumerable.Range(0, names.Count).ToArray() : occurrences;
        return new SqlParameterPlan(sql, SqlParameterParser.Rewrite(sql, canonical, style), names, slots, used);
    }

    /// <summary>The name of the parameter at <paramref name="slot"/> of <see cref="Slots"/>.</summary>
    internal string ParameterName(int slot) => Names[Slots[slot]];

    /// <summary>The explicit <c>param.name</c> value for <paramref name="name"/>, if one is configured.</summary>
    internal bool TryGetExplicit(string name, out ExplicitSqlParameter parameter) => _explicit.TryGetValue(name, out parameter);
}

/// <summary>An explicit <c>param.name</c> value: a constant, or a <c>${...}</c> expression evaluated per exchange.</summary>
internal readonly struct ExplicitSqlParameter(string value)
{
    /// <summary>The configured text.</summary>
    internal string Value { get; } = value;

    /// <summary>The value contains a <c>${...}</c> expression.</summary>
    internal bool IsExpression { get; } = value.Contains("${", StringComparison.Ordinal);

    /// <summary>
    /// The value to bind; an expression is evaluated against <paramref name="exchange"/>. An explicit parameter always has a
    /// value: an expression that yields null, or an empty constant, binds NULL.
    /// </summary>
    internal object Resolve(IExchange? exchange) => SqlParameterBinder.ResolveParamValue(Value, exchange);
}
