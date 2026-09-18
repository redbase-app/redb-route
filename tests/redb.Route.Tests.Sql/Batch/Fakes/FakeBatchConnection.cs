using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace redb.Route.Tests.Sql.Batch.Fakes;

/// <summary>
/// A connection whose command executions are scripted per execution index, and which counts what the connector
/// asks of it. Lets batch algorithms be tested at the exact point where a real provider would fail or be cancelled.
/// </summary>
internal sealed class FakeBatchConnection : DbConnection
{
    private string _connectionString = "";
    private int _executions;

    /// <summary>Called for every execution with its zero-based index; returns rows affected or throws.</summary>
    public Func<int, CancellationToken, int>? OnExecute { get; init; }

    /// <summary>How the connection's transactions answer savepoint calls.</summary>
    public SavepointBehavior Savepoints { get; init; } = SavepointBehavior.Unsupported;

    /// <summary>Rolling back a transaction of this connection throws.</summary>
    public bool FailRollback { get; init; }

    /// <summary>The connection was disposed.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Commands created through <see cref="DbConnection.CreateCommand"/>.</summary>
    public int CommandsCreated { get; private set; }

    /// <summary>Statements executed, successful or not.</summary>
    public int Executions => _executions;

    /// <summary>The last transaction begun on this connection.</summary>
    public FakeBatchTransaction? Transaction { get; private set; }

    /// <summary>The connection can create a <see cref="DbBatch"/> (<see cref="DbConnection.CanCreateBatch"/>).</summary>
    public bool SupportsBatch { get; init; }

    /// <summary>Batch commands create their own parameters (<see cref="DbBatchCommand.CanCreateParameter"/>).</summary>
    public bool BatchCommandsCanCreateParameter { get; init; } = true;

    /// <summary>Called for every batch execution with its zero-based index and the batch; returns rows affected or throws.</summary>
    public Func<int, FakeDbBatch, int>? OnExecuteBatch { get; init; }

    /// <summary>Number of commands in each executed batch, in order.</summary>
    public List<int> BatchSizes { get; } = [];

    /// <summary>Parameters created through <see cref="DbBatchCommand.CreateParameter"/>.</summary>
    public int BatchParametersCreated { get; internal set; }

    public override bool CanCreateBatch => SupportsBatch;

    protected override DbBatch CreateDbBatch() =>
        SupportsBatch ? new FakeDbBatch(this) : throw new NotSupportedException("fake: this connection has no DbBatch");

    internal int ExecuteBatch(FakeDbBatch batch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var index = BatchSizes.Count;
        BatchSizes.Add(batch.BatchCommands.Count);
        return OnExecuteBatch?.Invoke(index, batch) ?? batch.BatchCommands.Count;
    }

    /// <summary>
    /// Called for every batch executed for its rows, with its zero-based index and the batch; returns a reader over one
    /// result set per command, or throws.
    /// </summary>
    public Func<int, FakeDbBatch, DbDataReader>? OnExecuteBatchReader { get; init; }

    internal DbDataReader ExecuteBatchReader(FakeDbBatch batch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var index = BatchSizes.Count;
        BatchSizes.Add(batch.BatchCommands.Count);
        return OnExecuteBatchReader?.Invoke(index, batch)
               ?? throw new NotSupportedException("fake: no OnExecuteBatchReader script for a batch executed for its rows");
    }

    [AllowNull]
    public override string ConnectionString
    {
        get => _connectionString;
        set => _connectionString = value ?? "";
    }

    public override string Database => "fake";
    public override string DataSource => "fake";
    public override string ServerVersion => "0";
    public override ConnectionState State => ConnectionState.Open;
    public override void ChangeDatabase(string databaseName) { }
    public override void Close() { }
    public override void Open() { }

    /// <summary>The text of every command execution, in order.</summary>
    public List<string> ExecutedCommandTexts { get; } = [];

    internal int Execute(string commandText, CancellationToken ct)
    {
        ExecutedCommandTexts.Add(commandText);
        var index = _executions++;
        return OnExecute?.Invoke(index, ct) ?? 1;
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        Transaction = new FakeBatchTransaction(this, isolationLevel);

    protected override DbCommand CreateDbCommand()
    {
        CommandsCreated++;
        return new FakeBatchCommand(this);
    }

    protected override void Dispose(bool disposing)
    {
        IsDisposed = true;
        base.Dispose(disposing);
    }
}
