using System.Data.Common;
using System.Globalization;
using System.Transactions;
using redb.Route.Abstractions;
using redb.Route.Sql.Connection;

namespace redb.Route.Sql.Repositories;

/// <summary>
/// Idempotent repository backed by a raw ADO.NET table.
/// For autonomous use of redb.Route without redb.Core dependency.
/// Supports auto-create table and TTL-based cleanup.
/// <para>
/// <b>Schema bootstrap:</b> when <see cref="SqlIdempotentOptions.CreateTable"/> is true (default),
/// the table is created on first operation with dialect-aware DDL: SQLite/PostgreSQL/MySQL/MariaDB use
/// <c>CREATE TABLE IF NOT EXISTS</c> with <c>VARCHAR</c> key columns (128 for the processor name, 255 for
/// the message key — MySQL refuses <c>TEXT</c> in a key); SQL Server (which has no such syntax) is detected
/// from the live connection and gets a guarded <c>IF OBJECT_ID(...) IS NULL CREATE TABLE</c> with
/// <c>NVARCHAR</c>/<c>INT</c> columns of the same sizes, inside the 900-byte clustered key limit. Set
/// <see cref="SqlIdempotentOptions.CreateTable"/> = false to manage the schema yourself.
/// </para>
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

    // What the connections of this repository do in an ambient transaction: 0 until the first one is seen.
    private const int Joins = 1;
    private const int StaysOutside = 2;
    private int _ambientBehaviour;

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

    /// <summary>
    /// True when the repository's connections enlist in an ambient transaction, so that <see cref="Add"/> inside a
    /// <c>.Transacted()</c> block is rolled back with it: Npgsql (PostgreSQL) and SqlClient (SQL Server), unless the
    /// connection string sets <c>Enlist=false</c>. False for other providers (Microsoft.Data.Sqlite does not enlist) and
    /// with <c>Enlist=false</c>; the idempotent consumer then removes the key itself after a rollback. It is learnt from the
    /// connection <see cref="Add"/> opens, and the consumer reads it after <see cref="Add"/>; before the first connection it
    /// is false.
    /// </summary>
    public bool JoinsAmbientTransaction => Volatile.Read(ref _ambientBehaviour) == Joins;

    /// <inheritdoc />
    public async Task<bool> Add(string key, CancellationToken ct = default)
    {
        await EnsureTableAsync(ct).ConfigureAwait(false);
        await CleanupIfNeededAsync(ct).ConfigureAwait(false);

        await using var conn = await _connectionFactory.CreateConnectionAsync(ct: ct).ConfigureAwait(false);
        if (Volatile.Read(ref _ambientBehaviour) == 0)
            Volatile.Write(ref _ambientBehaviour, EnlistsInAmbientTransaction(conn) ? Joins : StaysOutside);
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

            // The table is the repository's, not the message's: the first call may run inside the transaction of whatever
            // message came first, and DDL there would go with its rollback (PostgreSQL, SQL Server) or commit it implicitly
            // (MySQL, Oracle). As Apache Camel creates the table outside any exchange, it is created outside the transaction.
            using var outsideTransaction = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
            await using var conn = await _connectionFactory.CreateConnectionAsync(ct: ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();

            // Dialect-aware bootstrap. SQL Server has no CREATE TABLE IF NOT EXISTS, so it gets a
            // guarded T-SQL statement; SQLite/PostgreSQL/MySQL keep the portable form. Both are
            // idempotent at the SQL level, so even concurrent first-callers won't error — but we
            // serialize via _ddlLock to avoid burning round-trips and keep the
            // "set _tableCreated only after success" logic linear.
            cmd.CommandText = BuildCreateTableDdl(conn, _options.TableName);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            outsideTransaction.Complete();
            _tableCreated = true;
        }
        finally
        {
            _ddlLock.Release();
        }
    }

    /// <summary>True when the live connection is a SQL Server connection (exact class-name match,
    /// so <c>MySqlConnection</c>/<c>NpgsqlConnection</c>/<c>SqliteConnection</c> are not misdetected).</summary>
    private static bool IsSqlServer(DbConnection conn) =>
        string.Equals(conn.GetType().Name, "SqlConnection", StringComparison.Ordinal);

    /// <summary>
    /// Whether <paramref name="conn"/> enlists in an ambient transaction. Npgsql and SqlClient do by default (<c>Enlist</c> is
    /// on) and are the providers this is established for; any other provider, and <c>Enlist=false</c> or <c>no</c>, is taken as
    /// staying outside, so the consumer removes the key itself rather than leave one whose work never committed.
    /// </summary>
    private static bool EnlistsInAmbientTransaction(DbConnection conn)
    {
        if (conn.GetType().Name is not ("NpgsqlConnection" or "SqlConnection"))
            return false;

        var settings = new DbConnectionStringBuilder { ConnectionString = conn.ConnectionString };
        return !settings.TryGetValue("Enlist", out var enlist)
            || Convert.ToString(enlist, CultureInfo.InvariantCulture)?.Trim().ToLowerInvariant() is not ("false" or "no");
    }

    /// <summary>
    /// Returns the CREATE TABLE statement for the connection's dialect. The key columns are sized: MySQL and MariaDB refuse a
    /// <c>TEXT</c> column in a primary key (error 1170), and on SQL Server a clustered key is limited to 900 bytes — a
    /// processor name of 128 and a message key of 255 characters (766 bytes as <c>NVARCHAR</c>) stay inside it.
    /// </summary>
    private static string BuildCreateTableDdl(DbConnection conn, string table) => IsSqlServer(conn)
        ? $"""
            IF OBJECT_ID(N'{table}', N'U') IS NULL
            CREATE TABLE {table} (
                processor_name  NVARCHAR(128) NOT NULL,
                message_key     NVARCHAR(255) NOT NULL,
                created_at      NVARCHAR(64)  NOT NULL,
                confirmed       INT           NOT NULL DEFAULT 0,
                PRIMARY KEY (processor_name, message_key)
            )
            """
        : $"""
            CREATE TABLE IF NOT EXISTS {table} (
                processor_name  VARCHAR(128) NOT NULL,
                message_key     VARCHAR(255) NOT NULL,
                created_at      VARCHAR(64)  NOT NULL,
                confirmed       INTEGER      NOT NULL DEFAULT 0,
                PRIMARY KEY (processor_name, message_key)
            )
            """;

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
