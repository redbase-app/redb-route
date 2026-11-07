using System.Data.Common;
using redb.Route.Core;
using redb.Route.Sql;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.E2E.Base;

/// <summary>
/// The connector's batch against a real database: whatever the error mode, the outcome the route sees must match
/// what is persisted. A successful exchange reports exactly the rows written; a failed exchange leaves nothing
/// behind; a failure surfaces the provider's own error.
/// </summary>
public abstract class BatchFailureSemanticsTestsBase : IAsyncLifetime
{
    private SqlE2EDatabase? _db;

    /// <summary>The provider under test.</summary>
    protected abstract SqlE2EProvider Provider { get; }

    private SqlE2EDatabase Db => _db ?? throw new InvalidOperationException("The table is created in InitializeAsync.");

    /// <inheritdoc />
    public async Task InitializeAsync() => _db = await SqlE2EDatabase.CreateAsync(Provider);

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        try
        {
            if (_db is not null)
                await _db.DisposeAsync();
        }
        finally
        {
            (Provider as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public async Task Batch_ReportedCount_EqualsPersistedRows()
    {
        var (exchange, thrown) = await RunBatchAsync(Provider.InsertSql(Db.Table), Provider.DuplicateKeyItems(), breakOnError: false);

        await AssertOutcomeMatchesDatabaseAsync(exchange, thrown);
    }

    [Fact]
    public async Task Batch_TransactionKillingError_WritesNothingOutsideTransaction()
    {
        var (exchange, thrown) = await RunBatchAsync(
            Provider.TransactionKillingBatchSql(Db.Table), Provider.TransactionKillingItems(), breakOnError: false);

        await AssertOutcomeMatchesDatabaseAsync(exchange, thrown);
    }

    [Fact]
    public async Task BreakOnError_DoomedTransaction_ThrowsOriginalError()
    {
        var (_, thrown) = await RunBatchAsync(
            Provider.TransactionKillingBatchSql(Db.Table), Provider.TransactionKillingItems(), breakOnError: true);

        thrown.Should().NotBeNull("the middle item fails and the batch breaks on the first error");
        var providerError = thrown as DbException
            ?? (thrown as AggregateException)?.InnerExceptions.OfType<DbException>().FirstOrDefault();
        providerError.Should().NotBeNull(
            $"the failing item's provider error must reach the route, but the batch threw {Outcome.Describe(thrown)}");
        (await Db.ReadIdsAsync()).Should().BeEmpty("a batch that breaks on error rolls everything back");
    }

    // ── Decided error modes (17.0b, reference: Apache Camel) ─────────────────

    [Fact]
    public async Task BreakOnError_DuplicateKey_ThrowsProviderExceptionUnwrapped()
    {
        var (_, thrown) = await RunBatchAsync(Provider.InsertSql(Db.Table), Provider.DuplicateKeyItems(), breakOnError: true);

        thrown.Should().BeAssignableTo<DbException>(
            $"the provider's exception reaches the route as is, as in Camel, but the batch threw {Outcome.Describe(thrown)}");
        (await Db.ReadIdsAsync()).Should().BeEmpty("a batch that breaks on error rolls everything back");
    }

    [Fact]
    public async Task BatchContinue_DuplicateKey_CommitsOthersOrFailsCleanly()
    {
        var (exchange, thrown) = await RunBatchAsync(Provider.InsertSql(Db.Table), Provider.DuplicateKeyItems(), breakOnError: false);
        var persisted = await Db.ReadIdsAsync();

        if (Provider.DuplicateKeyDestroysTransaction)
        {
            thrown.Should().BeAssignableTo<DbException>(
                $"the error ended the transaction, so the batch fails with the item's own error, but it threw {Outcome.Describe(thrown)}");
            persisted.Should().BeEmpty();
            return;
        }

        thrown.Should().BeNull($"a statement-level error is undone to its savepoint and the batch goes on ({Outcome.Describe(thrown)})");
        persisted.Should().Equal(new[] { 1, 3 }, "the items around the failed one are committed");
        SqlBatchRun.BatchErrorIndexes(exchange).Should().Equal(new[] { 1 }, "the failed item is reported by its index");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private Task<(Exchange Exchange, Exception? Thrown)> RunBatchAsync(
        string sql, List<Dictionary<string, object?>> items, bool breakOnError) =>
        SqlBatchRun.RunAsync(Db, sql, items, breakOnError);

    private async Task AssertOutcomeMatchesDatabaseAsync(Exchange exchange, Exception? thrown)
    {
        var persisted = await Db.ReadIdsAsync();
        var rows = "[" + string.Join(",", persisted) + "]";

        if (thrown is not null)
        {
            persisted.Should().BeEmpty(
                $"a batch that fails must leave nothing behind, but rows {rows} were written although it threw {Outcome.Describe(thrown)}");
            return;
        }

        Convert.ToInt32(exchange.In.Headers[SqlHeaders.RowCount]).Should().Be(persisted.Length,
            $"the batch reported success, so its row count must match what is persisted (rows {rows}, " +
            $"error header: {(exchange.In.Headers.TryGetValue(SqlHeaders.Error, out var error) ? error : "none")})");
    }
}
