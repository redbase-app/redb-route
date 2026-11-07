using System.Collections.Concurrent;

namespace redb.Route.Sql;

/// <summary>
/// Registry for named SQL queries. Queries are registered at startup and referenced
/// by name using the <c>ref:name</c> syntax in SQL properties.
/// Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </summary>
public interface ISqlNamedQueryRegistry
{
    /// <summary>Registers a named query.</summary>
    /// <param name="name">Query name (case-insensitive).</param>
    /// <param name="sql">SQL text.</param>
    void Register(string name, string sql);

    /// <summary>Resolves a named query. Throws if not found.</summary>
    /// <param name="name">Query name.</param>
    /// <returns>SQL text.</returns>
    string Resolve(string name);

    /// <summary>Attempts to resolve a named query.</summary>
    /// <param name="name">Query name.</param>
    /// <param name="sql">SQL text if found.</param>
    /// <returns>True if the query was found.</returns>
    bool TryResolve(string name, out string? sql);

    /// <summary>Returns all registered queries.</summary>
    IReadOnlyDictionary<string, string> GetAll();

    /// <summary>
    /// Resolves a SQL string that may contain a <c>ref:name</c> reference.
    /// If the string starts with "ref:", resolves to the registered query.
    /// Otherwise, returns the original string unchanged.
    /// </summary>
    /// <param name="sql">SQL or ref:name reference.</param>
    /// <returns>Resolved SQL text.</returns>
    string ResolveRef(string sql);
}

/// <summary>
/// Default named query registry. Thread-safe, case-insensitive name matching.
/// </summary>
public sealed class SqlNamedQueryRegistry : ISqlNamedQueryRegistry
{
    /// <summary>Prefix for named query references in SQL properties.</summary>
    public const string RefPrefix = "ref:";

    private readonly ConcurrentDictionary<string, string> _queries = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public void Register(string name, string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        if (!_queries.TryAdd(name, sql))
            throw new ArgumentException($"Named query '{name}' is already registered.");
    }

    /// <inheritdoc />
    public string Resolve(string name)
    {
        if (_queries.TryGetValue(name, out var sql))
            return sql;

        throw new KeyNotFoundException(
            $"Named query '{name}' is not registered. " +
            "Register it via AddNamedQuery() in AddRedbRouteSql() configuration.");
    }

    /// <inheritdoc />
    public bool TryResolve(string name, out string? sql) =>
        _queries.TryGetValue(name, out sql);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> GetAll() =>
        _queries.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves a SQL string that may contain a <c>ref:name</c> reference.
    /// If the string starts with "ref:", resolves to the registered query.
    /// Otherwise, returns the original string unchanged.
    /// </summary>
    /// <param name="sql">SQL or ref:name reference.</param>
    /// <returns>Resolved SQL text.</returns>
    public string ResolveRef(string sql)
    {
        if (sql.StartsWith(RefPrefix, StringComparison.OrdinalIgnoreCase))
            return Resolve(sql[RefPrefix.Length..]);

        return sql;
    }
}
