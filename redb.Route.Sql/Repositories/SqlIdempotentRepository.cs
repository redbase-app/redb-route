using System.Data.Common;
using redb.Route.Abstractions;
using redb.Route.Sql.Connection;

namespace redb.Route.Sql.Repositories;

/// <summary>
/// Idempotent repository backed by a raw ADO.NET table.
/// For autonomous use of redb.Route without redb.Core dependency.
/// Supports auto-create table and TTL-based cleanup.
/// <para>
/// <b>Schema bootstrap:</b> when <see cref="SqlIdempotentOptions.CreateTable"/> is true (default),
/// the table is created on first operation via <c>CREATE TABLE IF NOT EXISTS</c>. The DDL
/// works with SQLite and PostgreSQL natively. <b>SQL Server does not support
/// <c>CREATE TABLE IF NOT EXISTS</c></b>; if you target SQL Server, set
/// <see cref="SqlIdempotentOptions.CreateTable"/> = false and create the table manually using:
/// </para>
/// <code>
/// CREATE TABLE [{TableName}] (
///   processor_name NVARCHAR(255) NOT NULL,
///   message_key    NVARCHAR(255) NOT NULL,
///   created_at     NVARCHAR(64)  NOT NULL,
///   confirmed      INT           NOT NULL DEFAULT 0,
///   PRIMARY KEY (processor_name, message_key)
/// );
/// </code>
/// <para>
/// <b>Cleanup:</b> when <see cref="SqlIdempotentOptions.Ttl"/> is set, every <see cref="Add"/>
/// runs a best-effort <c>DELETE WHERE created_at &lt; cutoff</c>. This adds latency to the hot
/// path; for high-throughput scenarios prefer running cleanup out-of-band on a schedule.
/// </para>
/// </summary>
public sealed class SqlIdempotentRepository : IIdempotentRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly SqlIdempotentOptions _options;
    private readonly SemaphoreSlim _ddlLock = new(1, 1);
    private bool _tableCreated;

    /// <summary>Creates an idempotent repository with the given connection factory and options.</summary>
    /// <param name="connectionFactory">Factory for creating database connections.</param>
    /// <param name="options">Repository configuration.</param>
    public SqlIdempotentRepository(ISqlConnectionFactory connectionFactory, SqlIdempotentOptions options)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(options);
        _connectionFactory = connectionFactory;
        _options = options;
    }

    /// <inheritdoc />
    public async Task<bool> Add(string key, CancellationToken ct = default)
    {
        await EnsureTableAsync(ct).ConfigureAwait(false);
        await CleanupIfNeededAsync(ct).ConfigureAwait(false);

        await using var conn = await _connectionFactory.CreateConnectionAsync(ct: ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = $"""
            INSERT INTO {_options.TableName} (processor_name, message_key, created_at, confirmed)
            SELECT @processor, @key, @now, 0
            WHERE NOT EXISTS (
                SELECT 1 FROM {_options.TableName}
                WHERE processor_name = @processor AND message_key = @key
            )
            """;

        AddParam(cmd, "processor", _options.ProcessorName);
        AddParam(cmd, "key", key);
        AddParam(cmd, "now", DateTimeOffset.UtcNow.ToString("O"));

        var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return rows > 0;
    }

    /// <inheritdoc />
    public async Task Confirm(string key, CancellationToken ct = default)
    {
        await EnsureTableAsync(ct).ConfigureAwait(false);
        await using var conn = await _connectionFactory.CreateConnectionAsync(ct: ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = $"""
            UPDATE {_options.TableName}
            SET confirmed = 1
            WHERE processor_name = @processor AND message_key = @key
            """;

        AddParam(cmd, "processor", _options.ProcessorName);
        AddParam(cmd, "key", key);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task Remove(string key, CancellationToken ct = default)
    {
        await EnsureTableAsync(ct).ConfigureAwait(false);
        await using var conn = await _connectionFactory.CreateConnectionAsync(ct: ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = $"""
            DELETE FROM {_options.TableName}
            WHERE processor_name = @processor AND message_key = @key
            """;

        AddParam(cmd, "processor", _options.ProcessorName);
        AddParam(cmd, "key", key);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> Contains(string key, CancellationToken ct = default)
    {
        await EnsureTableAsync(ct).ConfigureAwait(false);
        await using var conn = await _connectionFactory.CreateConnectionAsync(ct: ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = $"""
            SELECT COUNT(1) FROM {_options.TableName}
            WHERE processor_name = @processor AND message_key = @key
            """;

        AddParam(cmd, "processor", _options.ProcessorName);
        AddParam(cmd, "key", key);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(result) > 0;
    }

    /// <inheritdoc />
    public async Task Clear(CancellationToken ct = default)
    {
        await EnsureTableAsync(ct).ConfigureAwait(false);
        await using var conn = await _connectionFactory.CreateConnectionAsync(ct: ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = $"DELETE FROM {_options.TableName} WHERE processor_name = @processor";
        AddParam(cmd, "processor", _options.ProcessorName);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task EnsureTableAsync(CancellationToken ct)
    {
        if (_tableCreated || !_options.CreateTable) return;

        await _ddlLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_tableCreated) return;

            await using var conn = await _connectionFactory.CreateConnectionAsync(ct: ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();

            // Portable DDL (SQLite + PostgreSQL). For SQL Server set CreateTable=false and run DDL manually
            // (see class-level remarks). CREATE TABLE IF NOT EXISTS is idempotent at the SQL level, so
            // even concurrent first-callers won't error — but we serialize via _ddlLock to avoid burning
            // round-trips and to make the "set _tableCreated only after success" logic linear.
            cmd.CommandText = $"""
                CREATE TABLE IF NOT EXISTS {_options.TableName} (
                    processor_name  TEXT    NOT NULL,
                    message_key     TEXT    NOT NULL,
                    created_at      TEXT    NOT NULL,
                    confirmed       INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (processor_name, message_key)
                )
                """;

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _tableCreated = true;
        }
        finally
        {
            _ddlLock.Release();
        }
    }

    private async Task CleanupIfNeededAsync(CancellationToken ct)
    {
        if (_options.Ttl is not { } ttl) return;

        await using var conn = await _connectionFactory.CreateConnectionAsync(ct: ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = $"""
            DELETE FROM {_options.TableName}
            WHERE processor_name = @processor AND created_at < @cutoff
            """;

        AddParam(cmd, "processor", _options.ProcessorName);
        AddParam(cmd, "cutoff", DateTimeOffset.UtcNow.Subtract(ttl).ToString("O"));

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
