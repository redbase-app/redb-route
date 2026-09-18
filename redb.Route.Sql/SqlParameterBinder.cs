using System.Data.Common;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using redb.Route.Abstractions;
using redb.Route.Expressions;
using redb.Route.Sql.Batch;

namespace redb.Route.Sql;

/// <summary>
/// Binds the <c>@name</c> placeholders of a statement to values taken from an exchange. One implementation serves the
/// single-statement producer, batch items, the poll consumer and the procedure producer, so the value sources and their
/// normalisation cannot drift apart between them.
/// </summary>
internal static class SqlParameterBinder
{
    private static readonly JsonSerializerOptions RawJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Rewrites the command text into the provider's <paramref name="style"/> and adds its parameters — one per distinct
    /// <c>:#name</c>, or one per occurrence for a positional style. Sources, first match wins: explicit <c>param.name</c>
    /// (constant or <c>${...}</c> expression), exchange header, key of a map body (a dictionary or a JSON object — as in Apache
    /// Camel, a POCO, XML or text body is not read by name). A placeholder found in none of them is an error, as in Camel; a
    /// source that is present with a null value binds NULL.
    /// </summary>
    /// <exception cref="InvalidOperationException">A placeholder has no value in any source, or its form is not supported.</exception>
    internal static void Bind(
        DbCommand command, IExchange exchange, IReadOnlyDictionary<string, string> explicitParameters, SqlPlaceholderStyle style,
        bool backslashEscapes)
    {
        var plan = SqlParameterPlan.Create(command.CommandText, explicitParameters, style, backslashEscapes);
        var values = new object?[plan.Names.Count];

        for (var index = 0; index < plan.Names.Count; index++)
        {
            var name = plan.Names[index];
            if (plan.TryGetExplicit(name, out var explicitParameter))
            {
                values[index] = explicitParameter.Resolve(exchange);
            }
            else if (!TryGetExchangeValue(exchange, name, out values[index]))
            {
                throw new InvalidOperationException(MissingValueMessage(name, plan.SourceSql,
                    $"no param.{name} option, no '{name}' header and no '{name}' key in the message body"));
            }
        }

        command.CommandText = plan.Sql;
        for (var slot = 0; slot < plan.Slots.Count; slot++)
            Add(command, plan.ParameterName(slot), values[plan.Slots[slot]]);
    }

    /// <summary>Adds the parameter <paramref name="name"/> with <paramref name="value"/> normalised for the provider.</summary>
    internal static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = NormalizeForDb(value);
        command.Parameters.Add(parameter);
    }

    /// <summary>The message for a placeholder that has no value: where it was looked for, and the statement.</summary>
    internal static string MissingValueMessage(string name, string sql, string lookedAt) =>
        $"SQL parameter ':#{name}' has no value: {lookedAt}. Set param.{name}=... or supply the value; a value that is present " +
        $"but null binds NULL. Query: {sql}";

    /// <summary>
    /// Normalises C# values for ADO.NET parameter binding:
    /// <list type="bullet">
    ///   <item><c>null</c> → <see cref="DBNull.Value"/>.</item>
    ///   <item>Empty string (<c>""</c>) → <see cref="DBNull.Value"/>. A null upstream value
    ///   (e.g. an OAuth <c>client_id</c> absent from a logout request) is sometimes
    ///   serialised through string-typed plumbing (header, query-string, JSON DTO) as
    ///   <see cref="string.Empty"/>; if we bind that literally, audit columns receive
    ///   <c>""</c> instead of <c>NULL</c> and downstream <c>WHERE ... IS NULL</c>
    ///   predicates miss the rows. Treating empty string as NULL at the parameter layer
    ///   keeps schema invariants honest (NULL = "not specified") without forcing every
    ///   caller to convert.</item>
    ///   <item>JSON values (<see cref="JsonElement"/>, <see cref="JsonNode"/> — what unmarshalling JSON to <c>object</c>
    ///   produces), which providers cannot bind: a string, number (<c>long</c>, else <c>decimal</c>, else <c>double</c>) or
    ///   boolean becomes that CLR value, JSON null becomes <see cref="DBNull.Value"/>, an object or array becomes its JSON text.</item>
    ///   <item>Everything else passes through unchanged.</item>
    /// </list>
    /// </summary>
    internal static object NormalizeForDb(object? value) => value switch
    {
        null => DBNull.Value,
        string { Length: 0 } => DBNull.Value,
        JsonElement element => FromJson(element),
        JsonValue node when node.TryGetValue<JsonElement>(out var element) => FromJson(element),
        JsonValue node => NormalizeForDb(node.GetValue<object>()),
        JsonNode node => node.ToJsonString(RawJson),
        _ => value
    };

    /// <summary>Resolves an explicit parameter value: <c>${...}</c> is evaluated against the exchange; empty means NULL.</summary>
    internal static object ResolveParamValue(string value, IExchange? exchange)
    {
        if (value.Contains("${") && exchange != null)
        {
            return ExpressionResolver.ResolveTypedOrTemplate(value, exchange) ?? DBNull.Value;
        }
        return string.IsNullOrEmpty(value) ? DBNull.Value : (object)value;
    }

    private static bool TryGetExchangeValue(IExchange exchange, string name, out object? value)
    {
        if (exchange.In.Headers.TryGetValue(name, out value))
            return true;

        return BatchItemAccessor.FindByKey(exchange.In.Body, name, out value) == ItemLookup.Found;
    }

    private static object FromJson(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return NormalizeForDb(element.GetString());
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var integer))
                    return integer;
                if (element.TryGetDecimal(out var number))
                    return number;
                if (element.TryGetDouble(out var real))
                    return real;
                return element.GetRawText();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return DBNull.Value;
            default:
                return element.GetRawText();
        }
    }
}
