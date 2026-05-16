using System.Data;
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
    public string? ConnectionString { get; set; }

    /// <summary>ADO.NET provider name (e.g. "Microsoft.Data.SqlClient", "Npgsql").</summary>
    public string? Provider { get; set; }

    /// <summary>Command execution timeout in seconds. Default: 30.</summary>
    public int CommandTimeout { get; set; } = 30;

    /// <summary>Whether to wrap execution in a <see cref="System.Data.Common.DbTransaction"/>.</summary>
    public bool Transacted { get; set; }

    /// <summary>Transaction isolation level. Null = provider default.</summary>
    public IsolationLevel? IsolationLevel { get; set; }

    // ── Query ─────────────────────────────────────────────────────────

    /// <summary>
    /// SQL query or <c>ref:name</c> reference to a named query.
    /// Supports <c>${...}</c> expressions via <see cref="DynamicValue{T}"/>.
    /// </summary>
    public DynamicValue<string>? Query { get; set; }

    /// <summary>
    /// How the result set is mapped to the Exchange body.
    /// Default is <see cref="SqlOutputType.Auto"/>: SELECT/WITH → SelectList, INSERT/UPDATE/DELETE → None.
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

    // ── Batch ─────────────────────────────────────────────────────────

    /// <summary>Batch size for producer. 0 = all in one batch, &gt;0 = chunk size.</summary>
    public int BatchSize { get; set; }

    /// <summary>Stop the batch on first error instead of continuing.</summary>
    public bool BreakBatchOnError { get; set; }

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

        if (CommandTimeout < 0)
            throw new ArgumentException("CommandTimeout must be non-negative.");

        if (Delay < 0)
            throw new ArgumentException("Delay must be non-negative.");
    }
}
