using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using redb.Route.Abstractions;
using redb.Route.Sql.Connection;

namespace redb.Route.Sql.Repositories;

/// <summary>
/// SQL-backed implementation of <see cref="IClaimCheckRepository"/>.
/// Uses raw ADO.NET with <see cref="ISqlConnectionFactory"/> — works with any DbProvider
/// (SQLite, PostgreSQL, MSSQL, MySQL).
/// Auto-creates the table on first use. Supports TTL-based lazy cleanup.
/// </summary>
public sealed class SqlClaimCheckRepository : IClaimCheckRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly SqlClaimCheckOptions _options;
    private volatile bool _tableCreated;
    private int _storeCounter;

    /// <summary>
    /// Creates a new SQL-backed claim check repository.
    /// </summary>
    /// <param name="connectionFactory">Factory for creating database connections.</param>
    /// <param name="options">Repository options.</param>
    public SqlClaimCheckRepository(ISqlConnectionFactory connectionFactory, SqlClaimCheckOptions options)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public async Task<string> Store(ReadOnlyMemory<byte> data, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var key = Guid.NewGuid().ToString("N");
        await StoreInternal(key, data, ttl, ct).ConfigureAwait(false);
        return key;
    }

    /// <inheritdoc />
    public async Task Store(string key, ReadOnlyMemory<byte> data, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await StoreInternal(key, data, ttl, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<byte[]?> Retrieve(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await EnsureTableAsync(ct).ConfigureAwait(false);

        await using var conn = await _connectionFactory.CreateConnectionAsync(readOnly: true, ct: ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = $"""
            SELECT data FROM {_options.TableName}
            WHERE claim_key = @key
            AND (expires_at IS NULL OR expires_at > @now)
            """;

        AddParam(cmd, "key", key);
        AddParam(cmd, "now", DateTimeOffset.UtcNow.ToString("O"));

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result as byte[];
    }

    /// <inheritdoc />
    public async Task<byte[]?> RetrieveAndRemove(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await EnsureTableAsync(ct).ConfigureAwait(false);

        // SELECT + DELETE inside a transaction for atomicity
        await using var conn = await _connectionFactory.CreateConnectionAsync(ct: ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {
            // Read
            byte[]? data;
            await using (var readCmd = conn.CreateCommand())
            {
                readCmd.Transaction = tx;
                readCmd.CommandText = $"""
                    SELECT data FROM {_options.TableName}
                    WHERE claim_key = @key
                    AND (expires_at IS NULL OR expires_at > @now)
                    """;

                AddParam(readCmd, "key", key);
                AddParam(readCmd, "now", DateTimeOffset.UtcNow.ToString("O"));

                data = await readCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as byte[];
            }

            if (data is null)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return null;
            }

            // Delete
            await using (var delCmd = conn.CreateCommand())
            {
                delCmd.Transaction = tx;
                delCmd.CommandText = $"DELETE FROM {_options.TableName} WHERE claim_key = @key";
                AddParam(delCmd, "key", key);
                await delCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return data;
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task Remove(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await EnsureTableAsync(ct).ConfigureAwait(false);

        await using var conn = await _connectionFactory.CreateConnectionAsync(ct: ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = $"DELETE FROM {_options.TableName} WHERE claim_key = @key";
        AddParam(cmd, "key", key);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task StoreInternal(string key, ReadOnlyMemory<byte> data, TimeSpan? ttl, CancellationToken ct)
    {
        await EnsureTableAsync(ct).ConfigureAwait(false);
        await CleanupIfNeededAsync(ct).ConfigureAwait(false);

        var effectiveTtl = ttl ?? _options.DefaultTtl;
        var expiresAt = effectiveTtl.HasValue && effectiveTtl.Value > TimeSpan.Zero
            ? DateTimeOffset.UtcNow + effectiveTtl.Value
            : (DateTimeOffset?)null;

        await using var conn = await _connectionFactory.CreateConnectionAsync(ct: ct).ConfigureAwait(false);

        // Upsert: delete existing then insert (portable across DBs)
        await using (var delCmd = conn.CreateCommand())
        {
            delCmd.CommandText = $"DELETE FROM {_options.TableName} WHERE claim_key = @key";
            AddParam(delCmd, "key", key);
            await delCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var insCmd = conn.CreateCommand())
        {
            insCmd.CommandText = $"""
                INSERT INTO {_options.TableName} (claim_key, data, created_at, expires_at)
                VALUES (@key, @data, @now, @exp)
                """;

            AddParam(insCmd, "key", key);
            AddParam(insCmd, "data", data.ToArray());
            AddParam(insCmd, "now", DateTimeOffset.UtcNow.ToString("O"));
            AddParam(insCmd, "exp", expiresAt.HasValue ? expiresAt.Value.ToString("O") : DBNull.Value);

            await insCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        Interlocked.Increment(ref _storeCounter);
    }

    private async Task EnsureTableAsync(CancellationToken ct)
    {
        if (_tableCreated || !_options.CreateTable) return;

        await using var conn = await _connectionFactory.CreateConnectionAsync(ct: ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {_options.TableName} (
                claim_key   TEXT NOT NULL PRIMARY KEY,
                data        BLOB NOT NULL,
                created_at  TEXT NOT NULL,
                expires_at  TEXT NULL
            )
            """;

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _tableCreated = true;
    }

    private async Task CleanupIfNeededAsync(CancellationToken ct)
    {
        if (_options.CleanupInterval <= 0) return;
        if (_storeCounter % _options.CleanupInterval != 0) return;

        await using var conn = await _connectionFactory.CreateConnectionAsync(ct: ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = $"""
            DELETE FROM {_options.TableName}
            WHERE expires_at IS NOT NULL AND expires_at < @now
            """;

        AddParam(cmd, "now", DateTimeOffset.UtcNow.ToString("O"));
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
