using System.Data;
using System.Reflection;
using redb.Route.Core;

namespace redb.Route.Sql;

/// <summary>
/// Options for the SQL endpoint. Inherits <see cref="EndpointOptions"/> for automatic
/// URI parameter binding via <see cref="EndpointOptions.BindFromUri"/>.
/// <para>
/// This is the single source of truth — both Fluent API (<see cref="SqlBuilder"/>)
/// and URI strings converge here through <c>BindFromUri()</c>.
/// </para>
/// </summary>
public sealed class SqlEndpointOptions : EndpointOptions
{
    // ── Mode ──────────────────────────────────────────────────────────

    /// <summary>Operating mode: Poll (consumer), Execute (producer), Procedure (producer).</summary>
    public SqlMode Mode { get; set; } = SqlMode.Execute;

    // ── Connection ────────────────────────────────────────────────────

    /// <summary>Named DataSource from the registry (configured via <c>AddDataSource</c>).</summary>
    public string? DataSource { get; set; }

    /// <summary>Direct connection string (used if <see cref="DataSource"/> is not set).</summary>
    [Sensitive]
    public string? ConnectionString { get; set; }

    /// <summary>ADO.NET provider name (e.g. "Microsoft.Data.SqlClient", "Npgsql").</summary>
    public string? Provider { get; set; }

    /// <summary>Command execution timeout in seconds. Default: 30.</summary>
    public int CommandTimeout { get; set; } = 30;

    /// <summary>Whether to wrap execution in a <see cref="System.Data.Common.DbTransaction"/>.</summary>
    public bool Transacted { get; set; }

    /// <summary>Transaction isolation level. Null = provider default.</summary>
    public IsolationLevel? IsolationLevel { get; set; }

    /// <summary>
    /// Runs the statement on the data source's read replica (<see cref="Connection.SqlConnectionOptions.ReadConnectionString"/>),
    /// in <see cref="SqlMode.Execute"/>, <see cref="SqlMode.Procedure"/> and <see cref="SqlMode.Poll"/>. False, the default: the
    /// primary database, whatever the statement. Only the endpoint's author knows that a statement does not write — a SELECT
    /// can call a function that writes, advance a sequence or take a lock — so nothing is guessed from the SQL text. A replica
    /// may lag behind the primary.
    /// <para>
    /// Refused with <see cref="BatchSize"/> above zero, and for a poll with <see cref="OnSuccess"/>, <see cref="OnFailure"/>,
    /// <see cref="OnBatchComplete"/> or <see cref="Transacted"/>: rows read on a lagging replica and marked on the primary would
    /// be delivered again.
    /// </para>
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// How the <c>:#name</c> placeholders of a statement are written for the provider: <see cref="SqlPlaceholderStyle.At"/>
    /// (<c>@name</c>, the default), <see cref="SqlPlaceholderStyle.Colon"/> (<c>:name</c>, Oracle) or
    /// <see cref="SqlPlaceholderStyle.Question"/> (<c>?</c>, ODBC). Applies to every statement the endpoint sends — the query,
    /// a batch, <see cref="OnSuccess"/> / <see cref="OnFailure"/> / <see cref="OnBatchComplete"/>, a function call with
    /// <see cref="AsFunction"/>; a stored procedure called by name has no text to rewrite.
    /// </summary>
    public SqlPlaceholderStyle PlaceholderStyle { get; set; } = SqlPlaceholderStyle.At;

    /// <summary>
    /// Inside <c>'…'</c> and <c>"…"</c> of a statement a backslash escapes the next character, as MySQL and MariaDB read them
    /// unless the server's <c>sql_mode</c> has <c>NO_BACKSLASH_ESCAPES</c>. False, the default: standard SQL, where a backslash
    /// is an ordinary character and <c>'C:\'</c> is a complete literal. Nothing is guessed from the provider — the same text
    /// means different things on different databases, and on MySQL it depends on the server's mode. Without it, a
    /// <c>:#name</c> after <c>\'</c> is not found; doubling the quote (<c>''</c>) works everywhere. Applies to every statement
    /// <see cref="PlaceholderStyle"/> applies to.
    /// </summary>
    public bool BackslashEscapes { get; set; }

    // ── Query ─────────────────────────────────────────────────────────

    /// <summary>
    /// SQL query or <c>ref:name</c> reference to a named query.
    /// Supports <c>${...}</c> expressions via <see cref="DynamicValue{T}"/>.
    /// </summary>
    public DynamicValue<string>? Query { get; set; }

    /// <summary>
    /// How the result set is mapped to the Exchange body. The default, <see cref="SqlOutputType.Auto"/>, is decided by what the
    /// statement returns when it runs — as Apache Camel's <c>execute()</c> asks the driver, never by the text: a result set
    /// is delivered as <see cref="SqlOutputType.SelectList"/>, a statement without one sets the rows affected
    /// (<see cref="SqlOutputType.None"/>). In a batch <c>Auto</c> collects no rows.
    /// </summary>
    public SqlOutputType OutputType { get; set; } = SqlOutputType.Auto;

    /// <summary>POCO type name for <see cref="Mapping.PocoRowMapper{T}"/>. Null = Dictionary mapping.</summary>
    public string? OutputClass { get; set; }

    /// <summary>If set, result goes to this header instead of the Exchange body.</summary>
    public string? OutputHeader { get; set; }

    /// <summary>Dry-run mode: logs the query without executing it.</summary>
    public bool Noop { get; set; }

    // ── Polling (Mode = Poll) ─────────────────────────────────────────

    /// <summary>Delay between polls in milliseconds. Default: 500.</summary>
    public int Delay { get; set; } = 500;

    /// <summary>Initial delay before the first poll in milliseconds. Default: 1000.</summary>
    public int InitialDelay { get; set; } = 1000;

    /// <summary>Fixed-rate polling (delay is measured from start of poll, not end).</summary>
    public bool FixedRate { get; set; }

    /// <summary>Maximum number of poll cycles. 0 = unlimited.</summary>
    public long RepeatCount { get; set; }

    /// <summary>Maximum rows to process per poll. -1 = unlimited.</summary>
    public int MaxMessagesPerPoll { get; set; } = -1;

    /// <summary>Create an Exchange even when the result set is empty.</summary>
    public bool RouteEmptyResultSet { get; set; }

    /// <summary>Send an empty message when there is nothing to poll (idle notification).</summary>
    public bool SendEmptyMessageWhenIdle { get; set; }

    /// <summary>SQL to execute after each row is successfully processed.</summary>
    public string? OnSuccess { get; set; }

    /// <summary>SQL to execute when processing a row fails.</summary>
    public string? OnFailure { get; set; }

    /// <summary>SQL to execute after the entire batch is processed.</summary>
    public string? OnBatchComplete { get; set; }

    /// <summary>
    /// How polled rows reach the route. <see cref="SqlPollDelivery.PerRow"/> (the default): an exchange per row.
    /// <see cref="SqlPollDelivery.List"/> (Apache Camel <c>useIterator=false</c>): one exchange whose body is the list of rows —
    /// or, with <see cref="SqlOutputType.StreamList"/>, the open stream the route reads — with <see cref="OnSuccess"/> /
    /// <see cref="OnFailure"/> and <see cref="OnBatchComplete"/> run once, after the reader is closed; their values come from
    /// headers and <c>param.*</c>.
    /// </summary>
    public SqlPollDelivery PollDelivery { get; set; } = SqlPollDelivery.PerRow;

    // ── Batch ─────────────────────────────────────────────────────────

    /// <summary>
    /// Batch mode for the producer. A value above zero makes a list body a batch: the statement runs once per item and all
    /// items share one transaction (the route's, when the route is transacted). Zero, the default, runs the statement once
    /// with the body as it is. An empty list writes nothing.
    /// <para>
    /// The value is the number of statements sent in one round trip where the connection can create a
    /// <see cref="System.Data.Common.DbBatch"/> and the batch breaks on the first error; it never splits the transaction.
    /// </para>
    /// </summary>
    public int BatchSize { get; set; }

    /// <summary>
    /// What a failing batch item does.
    /// <para>
    /// <c>true</c> (the default, as in Apache Camel): the batch stops, the whole transaction rolls back and the provider's own
    /// exception is thrown, with the item's index in <see cref="Exception.Data"/> under <see cref="SqlHeaders.BatchFailedIndex"/>.
    /// </para>
    /// <para>
    /// <c>false</c>: every item runs under a savepoint; a failed item is undone and reported in
    /// <see cref="SqlHeaders.BatchErrors"/>, and the other items commit. This needs a local transaction with savepoints, so it
    /// is refused before anything is written inside a transacted route or on a provider without savepoints; and when the
    /// server has ended the transaction (an error that aborts it), the batch still stops with that item's error.
    /// </para>
    /// </summary>
    public bool BreakBatchOnError { get; set; } = true;

    // ── Explicit Parameters ─────────────────────────────────────────

    private IReadOnlyDictionary<string, string>? _explicitParameters;

    /// <summary>
    /// Explicit parameter bindings from <c>.Param()</c> calls.
    /// Extracted from URI parameters with <c>param.</c> prefix.
    /// Values may contain <c>${...}</c> expressions resolved at runtime.
    /// </summary>
    internal IReadOnlyDictionary<string, string> ExplicitParameters =>
        _explicitParameters ??= ParseExplicitParameters();

    private IReadOnlyDictionary<string, string> ParseExplicitParameters()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in UnmappedParameters)
        {
            if (key.StartsWith("param.", StringComparison.OrdinalIgnoreCase))
                dict[key["param.".Length..]] = value;
        }
        return dict;
    }

    // ── Procedure ─────────────────────────────────────────────────────

    /// <summary>Stored procedure or function name (for <see cref="SqlMode.Procedure"/>).</summary>
    public string? ProcedureName { get; set; }

    /// <summary>Call as a function (<c>SELECT function_name(...)</c>) instead of <c>EXEC/CALL</c>.</summary>
    public bool AsFunction { get; set; }

    /// <summary>
    /// Serialized procedure parameter definitions.
    /// Format: <c>IN:name:DbType,OUT:name:DbType,INOUT:name:DbType</c>.
    /// </summary>
    public string? ProcedureParams { get; set; }

    // ── Validation ────────────────────────────────────────────────────

    /// <inheritdoc />
    public override void Validate()
    {
        if (DataSource is null && ConnectionString is null)
            throw new ArgumentException("Either DataSource or ConnectionString is required.");

        if (Mode == SqlMode.Procedure && string.IsNullOrEmpty(ProcedureName))
            throw new ArgumentException("ProcedureName is required for Procedure mode.");

        if (AsFunction && ProcedureParams is { } definitions && definitions.Split(',').Any(definition =>
                definition.TrimStart().StartsWith("OUT:", StringComparison.OrdinalIgnoreCase) ||
                definition.TrimStart().StartsWith("INOUT:", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException(
                "asFunction=true runs SELECT name(...) and takes its result as the scalar: an OUT or INOUT parameter has no place " +
                "in that statement, and under a positional placeholder style it would shift the other parameters. Declare the " +
                "parameters IN, or call the procedure with asFunction=false.");

        if (Mode == SqlMode.Poll && PollDelivery == SqlPollDelivery.List && OutputType is SqlOutputType.Scalar or SqlOutputType.SelectOne)
            throw new ArgumentException(
                $"pollDelivery=List delivers the polled rows as one list, and outputType={OutputType} reads a single value. " +
                "Use outputType=SelectList or StreamList with pollDelivery=List, or pollDelivery=PerRow.");

        if (CommandTimeout < 0)
            throw new ArgumentException("CommandTimeout must be non-negative.");

        if (Delay < 0)
            throw new ArgumentException("Delay must be non-negative.");

        if (BatchSize < 0)
            throw new ArgumentException("BatchSize must be non-negative.");

        if (ReadOnly && BatchSize > 0)
            throw new ArgumentException(
                "readOnly=true sends the statement to the read replica, and batchSize makes it a batch that writes. " +
                "Remove readOnly or batchSize.");

        if (ReadOnly && Mode == SqlMode.Poll && (Transacted || !string.IsNullOrEmpty(OnSuccess) ||
                                                  !string.IsNullOrEmpty(OnFailure) || !string.IsNullOrEmpty(OnBatchComplete)))
            throw new ArgumentException(
                "readOnly=true polls the read replica, where onSuccess / onFailure / onBatchComplete and transacted=true cannot " +
                "mark or lock the rows; rows read on a lagging replica and marked on the primary would be delivered again. " +
                "Poll the primary database (no readOnly) when the poll marks its rows.");

        RejectUnconvertibleOptionValues();
    }

    /// <summary>
    /// A value given for one of this connector's own options that cannot be converted to the option's type is not an unknown
    /// parameter to pass over: the option would silently keep its default — a <c>${...}</c> expression for <c>batchSize</c>
    /// used to turn batching off and send the whole list to a single statement. Numeric and enum options take constants or
    /// <c>{{property}}</c> placeholders.
    /// </summary>
    private void RejectUnconvertibleOptionValues()
    {
        foreach (var (key, value) in UnmappedParameters)
        {
            var property = typeof(SqlEndpointOptions).GetProperty(key,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase | BindingFlags.DeclaredOnly);
            if (property is not { CanWrite: true })
                continue;

            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            throw new ArgumentException(
                $"Option '{key}' has the value '{value}', which cannot be converted to {type.Name}. " +
                "This option does not accept ${...} expressions; use a constant or a {{property}} placeholder.");
        }
    }
}
