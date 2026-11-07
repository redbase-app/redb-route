using System.Data.Common;
using Microsoft.Data.SqlClient;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.E2E.MsSql;

/// <summary>
/// Characterization: a <see cref="DbBatch"/> chosen as deadlock victim mid-batch. The server rolls the victim's
/// transaction back; the commands queued after the failing one in the same batch must not run in autocommit —
/// SqlClient does run a separate command after a server-side rollback, so this is not a given.
/// </summary>
[Trait("Category", "Integration")]
[Trait("SqlProvider", "mssql")]
[Trait("SqlSuite", "Characterization")]
public sealed class MsSqlDeadlockSemanticsTests : IAsyncLifetime
{
    private readonly MsSqlE2EProvider _provider = new(xactAbortOn: false);
    private SqlE2EDatabase? _db;

    private SqlE2EDatabase Db => _db ?? throw new InvalidOperationException("The table is created in InitializeAsync.");

    /// <inheritdoc />
    public async Task InitializeAsync() => _db = await SqlE2EDatabase.CreateAsync(_provider);

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_db is not null)
            await _db.DisposeAsync();
    }

    [Fact]
    public async Task DbBatch_DeadlockVictim_NoRowsOutsideTransaction()
    {
        var table = Db.Table;
        await Db.ExecuteAsync(_provider.InsertLiteral(table, 1, "a"));
        await Db.ExecuteAsync(_provider.InsertLiteral(table, 2, "b"));

        await using var holder = await Db.OpenAsync();
        await using var victim = await Db.OpenAsync();
        await using var watcher = await Db.OpenAsync();

        await SqlE2EDatabase.ExecuteAsync(victim, null, "SET DEADLOCK_PRIORITY LOW");
        var victimSession = await ScalarIntAsync(victim, "SELECT @@SPID");
        var holderSession = await ScalarIntAsync(holder, "SELECT @@SPID");

        await using var holderTx = await holder.BeginTransactionAsync();
        await SqlE2EDatabase.ExecuteAsync(holder, holderTx, $"UPDATE {table} SET val = 'holder' WHERE id = 2");

        await using var victimTx = await victim.BeginTransactionAsync();
        await using var batch = victim.CreateBatch();
        batch.Transaction = victimTx;
        foreach (var statement in new[]
                 {
                     _provider.InsertLiteral(table, 10, "victim"),
                     $"UPDATE {table} SET val = 'victim' WHERE id = 2",
                     _provider.InsertLiteral(table, 11, "victim"),
                 })
        {
            var command = batch.CreateBatchCommand();
            command.CommandText = statement;
            batch.BatchCommands.Add(command);
        }

        // The victim holds row 10 and waits for row 2; the holder then asks for row 10 — a cycle.
        var victimRun = batch.ExecuteNonQueryAsync();
        await WaitUntilBlockedAsync(watcher, victimSession, holderSession, TimeSpan.FromSeconds(20));
        var holderRun = SqlE2EDatabase.ExecuteAsync(holder, holderTx, $"UPDATE {table} SET val = 'holder' WHERE id = 10");

        var victimError = await Outcome.Of(() => victimRun);
        victimError.Should().BeOfType<SqlException>().Which.Number.Should().Be(1205, "the low-priority batch is the deadlock victim");

        await holderRun;
        await holderTx.CommitAsync();

        // The victim's transaction is gone on the server; its client-side rollback outcome is not the fact under test.
        await Outcome.Of(() => victimTx.RollbackAsync());

        (await Db.ReadIdsAsync()).Should().Equal(new[] { 1, 2 },
            "no command of the victim batch — before or after the deadlock — may survive outside its transaction");
    }

    private static async Task<int> ScalarIntAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    /// <summary>
    /// Polls the server's request table until <paramref name="session"/> waits for a lock held by <paramref name="blocker"/>;
    /// a deadline, not a fixed sleep. Matching the blocker and a lock wait matters: <c>blocking_session_id</c> is also
    /// non-zero (negative) for page-latch waits, which under load showed up before the victim reached its lock wait and let
    /// the holder run its statement too early — no cycle, and the victim timed out (-2) instead of being chosen (1205).
    /// </summary>
    private static async Task WaitUntilBlockedAsync(DbConnection watcher, int session, int blocker, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var blocked = await ScalarIntAsync(watcher,
                "SELECT COUNT(*) FROM sys.dm_exec_requests " +
                $"WHERE session_id = {session} AND blocking_session_id = {blocker} AND wait_type LIKE 'LCK[_]M[_]%'");
            if (blocked > 0)
                return;
            await Task.Delay(50);
        }

        throw new TimeoutException($"Session {session} did not wait for a lock held by session {blocker} within {timeout}.");
    }
}
