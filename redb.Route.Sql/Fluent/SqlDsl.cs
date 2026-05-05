using System.Data;
using System.Text;
using System.Web;
using redb.Route.Abstractions;

namespace redb.Route.Sql;

/// <summary>
/// Entry point for the SQL Fluent API. Three methods → three modes.
/// Each returns a <see cref="SqlBuilder"/> for fluent configuration.
/// <para>
/// The builder generates a URI string that is fully equivalent to writing the URI manually.
/// Both paths converge in <see cref="SqlEndpointOptions"/> via URI binding → same result.
/// </para>
/// <example>
/// <code>
/// // Fluent:
/// .From(Sql.Poll("SELECT * FROM outbox WHERE processed = 0")
///     .DataSource("main").Delay(5000).Transacted()
///     .OnSuccess("UPDATE outbox SET processed = 1 WHERE id = @id"))
///
/// // URI (equivalent):
/// .From("sql:SELECT * FROM outbox WHERE processed = 0?mode=Poll&amp;dataSource=main&amp;delay=5000&amp;transacted=true&amp;onSuccess=...")
/// </code>
/// </example>
/// </summary>
public static class Sql
{
    /// <summary>Creates a polling consumer that repeatedly executes a SELECT query.</summary>
    /// <param name="sql">SQL SELECT query or <c>ref:name</c> reference.</param>
    public static SqlBuilder Poll(string sql) => new(SqlMode.Poll, sql);

    /// <summary>Creates a producer that executes INSERT/UPDATE/DELETE/SELECT statements.</summary>
    /// <param name="sql">SQL statement or <c>ref:name</c> reference.</param>
    public static SqlBuilder Execute(string sql) => new(SqlMode.Execute, sql);

    /// <summary>Creates a producer that calls a stored procedure or function.</summary>
    /// <param name="name">Stored procedure or function name.</param>
    public static SqlBuilder Procedure(string name) => new(SqlMode.Procedure, name);
}

/// <summary>
/// Fluent builder for SQL endpoint URIs. Thin sugar over <see cref="SqlEndpointOptions"/>.
/// Call <see cref="Build"/> or use implicit conversion to <see cref="string"/>
/// to get the URI for <c>From()</c> / <c>To()</c>.
/// </summary>
public sealed class SqlBuilder
{
    private readonly SqlMode _mode;
    private readonly string _queryOrProcedure;
    private string? _dataSource;
    private string? _connectionString;
    private string? _provider;
    private string? _commandTimeout;
    private bool _transacted;
    private IsolationLevel? _isolationLevel;
    private SqlOutputType? _outputType;
    private string? _outputClass;
    private string? _outputHeader;
    private bool _noop;
    private string? _delay;
    private string? _initialDelay;
    private bool _fixedRate;
    private string? _repeatCount;
    private string? _maxMessagesPerPoll;
    private bool _routeEmptyResultSet;
    private bool _sendEmptyMessageWhenIdle;
    private string? _onSuccess;
    private string? _onFailure;
    private string? _onBatchComplete;
    private string? _batchSize;
    private bool _breakBatchOnError;
    private bool _asFunction;
    private readonly List<ProcedureParamDef> _procedureParams = [];
    private readonly List<(string Name, string Value)> _explicitParams = [];

    /// <summary>Creates a builder for the given mode and query/procedure name.</summary>
    internal SqlBuilder(SqlMode mode, string queryOrProcedure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryOrProcedure);
        _mode = mode;
        _queryOrProcedure = queryOrProcedure;
    }

    // ── Connection ────────────────────────────────────────────────────

    /// <summary>Sets the named data source.</summary>
    public SqlBuilder DataSource(IExpression name) { _dataSource = name.ToTemplateString(); return this; }

    /// <summary>Sets a direct connection string.</summary>
    public SqlBuilder ConnectionString(IExpression cs) { _connectionString = cs.ToTemplateString(); return this; }

    /// <summary>Sets the ADO.NET provider name.</summary>
    public SqlBuilder Provider(string name) { _provider = name; return this; }

    /// <summary>Sets the command timeout in seconds.</summary>
    public SqlBuilder CommandTimeout(int seconds) { _commandTimeout = seconds.ToString(); return this; }
    /// <summary>Sets the command timeout from an expression.</summary>
    public SqlBuilder CommandTimeout(IExpression seconds) { _commandTimeout = seconds.ToTemplateString(); return this; }

    // ── Transaction ───────────────────────────────────────────────────

    /// <summary>Enables transaction wrapping.</summary>
    public SqlBuilder Transacted() { _transacted = true; return this; }

    /// <summary>Sets the transaction isolation level.</summary>
    public SqlBuilder WithIsolationLevel(IsolationLevel level) { _isolationLevel = level; return this; }

    // ── Output ────────────────────────────────────────────────────────

    /// <summary>Sets how query results are mapped.</summary>
    public SqlBuilder OutputType(SqlOutputType type) { _outputType = type; return this; }

    /// <summary>Sets the POCO type name for result mapping.</summary>
    public SqlBuilder OutputClass(string typeName) { _outputClass = typeName; return this; }

    /// <summary>Sends the result to a header instead of the body.</summary>
    public SqlBuilder OutputHeader(IExpression headerName) { _outputHeader = headerName.ToTemplateString(); return this; }

    /// <summary>Enables dry-run mode.</summary>
    public SqlBuilder Noop() { _noop = true; return this; }

    // ── Polling ───────────────────────────────────────────────────────

    /// <summary>Sets the delay between polls in milliseconds.</summary>
    public SqlBuilder Delay(int ms) { _delay = ms.ToString(); return this; }
    /// <summary>Sets the delay from an expression.</summary>
    public SqlBuilder Delay(IExpression ms) { _delay = ms.ToTemplateString(); return this; }

    /// <summary>Sets the initial delay before the first poll.</summary>
    public SqlBuilder InitialDelay(int ms) { _initialDelay = ms.ToString(); return this; }
    /// <summary>Sets the initial delay from an expression.</summary>
    public SqlBuilder InitialDelay(IExpression ms) { _initialDelay = ms.ToTemplateString(); return this; }

    /// <summary>Enables fixed-rate polling.</summary>
    public SqlBuilder FixedRate() { _fixedRate = true; return this; }

    /// <summary>Sets the maximum number of poll cycles.</summary>
    public SqlBuilder RepeatCount(long count) { _repeatCount = count.ToString(); return this; }
    /// <summary>Sets the repeat count from an expression.</summary>
    public SqlBuilder RepeatCount(IExpression count) { _repeatCount = count.ToTemplateString(); return this; }

    /// <summary>Sets the maximum rows per poll.</summary>
    public SqlBuilder MaxMessagesPerPoll(int max) { _maxMessagesPerPoll = max.ToString(); return this; }
    /// <summary>Sets max messages per poll from an expression.</summary>
    public SqlBuilder MaxMessagesPerPoll(IExpression max) { _maxMessagesPerPoll = max.ToTemplateString(); return this; }

    /// <summary>Creates an Exchange even on empty result.</summary>
    public SqlBuilder RouteEmptyResultSet() { _routeEmptyResultSet = true; return this; }

    /// <summary>Sends idle notification when nothing to poll.</summary>
    public SqlBuilder SendEmptyMessageWhenIdle() { _sendEmptyMessageWhenIdle = true; return this; }

    // ── Lifecycle SQL ─────────────────────────────────────────────────

    /// <summary>SQL to execute after each row is successfully processed.</summary>
    public SqlBuilder OnSuccess(string sql) { _onSuccess = sql; return this; }

    /// <summary>SQL to execute when processing a row fails.</summary>
    public SqlBuilder OnFailure(string sql) { _onFailure = sql; return this; }

    /// <summary>SQL to execute after the entire batch.</summary>
    public SqlBuilder OnBatchComplete(string sql) { _onBatchComplete = sql; return this; }

    // ── Batch ─────────────────────────────────────────────────────────

    /// <summary>Sets the batch size for producer mode.</summary>
    public SqlBuilder Batch(int size) { _batchSize = size.ToString(); return this; }
    /// <summary>Sets the batch size from an expression.</summary>
    public SqlBuilder Batch(IExpression size) { _batchSize = size.ToTemplateString(); return this; }

    /// <summary>Stops the batch on first error.</summary>
    public SqlBuilder BreakBatchOnError() { _breakBatchOnError = true; return this; }

    // ── Explicit Parameters ─────────────────────────────────────────

    /// <summary>
    /// Binds a SQL parameter to an explicit value, overriding auto-bind from headers/body.
    /// Values containing <c>${...}</c> are resolved as expressions at runtime.
    /// </summary>
    /// <param name="name">Parameter name (with or without <c>@</c> prefix).</param>
    /// <param name="value">Constant value or expression string.</param>
    public SqlBuilder Param(string name, object? value)
    {
        var paramName = name.TrimStart('@');
        // null → "" → ResolveParamValue → DBNull.Value (SQL NULL)
        _explicitParams.Add((paramName, value?.ToString() ?? ""));
        return this;
    }

    /// <summary>
    /// Binds a SQL parameter to an expression, resolved at runtime.
    /// </summary>
    /// <param name="name">Parameter name (with or without <c>@</c> prefix).</param>
    /// <param name="expression">Expression that produces the parameter value.</param>
    public SqlBuilder Param(string name, IExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var paramName = name.TrimStart('@');
        _explicitParams.Add((paramName, expression.ToTemplateString()));
        return this;
    }

    // ── Procedure ─────────────────────────────────────────────────────

    /// <summary>Calls as a function instead of a procedure.</summary>
    public SqlBuilder AsFunction() { _asFunction = true; return this; }

    /// <summary>Adds an IN parameter definition.</summary>
    public SqlBuilder In(string name, DbType type)
    {
        _procedureParams.Add(new(SqlParamDirection.In, name, type, null));
        return this;
    }

    /// <summary>Adds an IN parameter with an expression value.</summary>
    public SqlBuilder In(string name, DbType type, string expression)
    {
        _procedureParams.Add(new(SqlParamDirection.In, name, type, expression));
        return this;
    }

    /// <summary>Adds an OUT parameter definition.</summary>
    public SqlBuilder Out(string name, DbType type)
    {
        _procedureParams.Add(new(SqlParamDirection.Out, name, type, null));
        return this;
    }

    /// <summary>Adds an INOUT parameter definition.</summary>
    public SqlBuilder InOut(string name, DbType type)
    {
        _procedureParams.Add(new(SqlParamDirection.InOut, name, type, null));
        return this;
    }

    // ── Build ─────────────────────────────────────────────────────────

    /// <summary>Builds the URI string for use in <c>From()</c> / <c>To()</c>.</summary>
    public string Build()
    {
        var sb = new StringBuilder();
        sb.Append("sql:");
        sb.Append(_queryOrProcedure);

        var separator = '?';

        void Append(string key, string value)
        {
            sb.Append(separator);
            sb.Append(key);
            sb.Append('=');
            sb.Append(HttpUtility.UrlEncode(value));
            separator = '&';
        }

        void AppendIf(string key, string? value)
        {
            if (value != null) Append(key, value);
        }

        void AppendBool(string key, bool value)
        {
            if (value) Append(key, "true");
        }

        // Mode (always present)
        Append("mode", _mode.ToString());

        // Connection
        AppendIf("dataSource", _dataSource);
        AppendIf("connectionString", _connectionString);
        AppendIf("provider", _provider);
        AppendIf("commandTimeout", _commandTimeout);
        AppendBool("transacted", _transacted);
        if (_isolationLevel.HasValue) Append("isolationLevel", _isolationLevel.Value.ToString());

        // Output
        if (_outputType.HasValue) Append("outputType", _outputType.Value.ToString());
        AppendIf("outputClass", _outputClass);
        AppendIf("outputHeader", _outputHeader);
        AppendBool("noop", _noop);

        // Polling
        AppendIf("delay", _delay);
        AppendIf("initialDelay", _initialDelay);
        AppendBool("fixedRate", _fixedRate);
        AppendIf("repeatCount", _repeatCount);
        AppendIf("maxMessagesPerPoll", _maxMessagesPerPoll);
        AppendBool("routeEmptyResultSet", _routeEmptyResultSet);
        AppendBool("sendEmptyMessageWhenIdle", _sendEmptyMessageWhenIdle);

        // Lifecycle SQL
        AppendIf("onSuccess", _onSuccess);
        AppendIf("onFailure", _onFailure);
        AppendIf("onBatchComplete", _onBatchComplete);

        // Batch
        AppendIf("batchSize", _batchSize);
        AppendBool("breakBatchOnError", _breakBatchOnError);

        // Explicit parameters
        foreach (var (name, value) in _explicitParams)
            Append($"param.{name}", value);

        // Procedure
        AppendBool("asFunction", _asFunction);
        if (_procedureParams.Count > 0)
        {
            var paramStr = string.Join(",", _procedureParams.Select(p => p.Serialize()));
            Append("procedureParams", paramStr);
        }

        return sb.ToString();
    }

    /// <summary>Implicit conversion to URI string.</summary>
    public static implicit operator string(SqlBuilder builder) => builder.Build();

    /// <inheritdoc />
    public override string ToString() => Build();

    // ── Internal ──────────────────────────────────────────────────────

    private readonly record struct ProcedureParamDef(SqlParamDirection Direction, string Name, DbType Type, string? Expression)
    {
        public string Serialize()
        {
            var dir = Direction.ToString().ToUpperInvariant();
            var result = $"{dir}:{Name}:{Type}";
            return Expression != null ? $"{result}:{Expression}" : result;
        }
    }
}
