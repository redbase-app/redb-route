using redb.Route.Abstractions;
using redb.Route.Expressions;

namespace redb.Route.Sql;

/// <summary>Headers every SQL producer path writes after it ran a statement.</summary>
internal static class SqlExchangeHeaders
{
    /// <summary>
    /// The header <c>outputHeader</c> names on this exchange: the option as written, or — when it is a <c>${...}</c>
    /// expression — its value on the exchange.
    /// </summary>
    /// <exception cref="InvalidOperationException">The expression yields no name.</exception>
    internal static string ResolveOutputHeader(string outputHeader, IExchange exchange)
    {
        if (!outputHeader.Contains("${", StringComparison.Ordinal))
            return outputHeader;

        var name = ExpressionResolver.ResolveTypedOrTemplate(outputHeader, exchange)?.ToString();
        if (string.IsNullOrEmpty(name))
            throw new InvalidOperationException($"outputHeader '{outputHeader}' yields no header name on this exchange.");
        return name;
    }

    /// <summary>
    /// Writes query text, execution time, data source and output type — the type the statement resolved to when
    /// <paramref name="resolvedOutputType"/> is given (<c>Auto</c> becomes what the driver returned), the option otherwise.
    /// </summary>
    internal static void SetCommon(
        IExchange exchange, SqlEndpointOptions options, string sql, long executionMs, SqlOutputType? resolvedOutputType = null)
    {
        exchange.In.Headers[SqlHeaders.Query] = sql;
        exchange.In.Headers[SqlHeaders.ExecutionTime] = executionMs;

        if (options.DataSource != null)
            exchange.In.Headers[SqlHeaders.DataSource] = options.DataSource;

        exchange.In.Headers[SqlHeaders.OutputType] = (resolvedOutputType ?? options.OutputType).ToString();
    }
}
