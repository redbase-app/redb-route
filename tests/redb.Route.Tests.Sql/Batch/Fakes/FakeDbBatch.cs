using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace redb.Route.Tests.Sql.Batch.Fakes;

/// <summary>
/// <see cref="DbBatch"/> of <see cref="FakeBatchConnection"/>: one execution is one round trip, handed to the connection's
/// batch script with the commands it carries.
/// </summary>
internal sealed class FakeDbBatch(FakeBatchConnection connection) : DbBatch
{
    private readonly FakeDbBatchCommandCollection _commands = new();

    public override int Timeout { get; set; }
    protected override DbBatchCommandCollection DbBatchCommands => _commands;
    protected override DbConnection? DbConnection { get; set; } = connection;
    protected override DbTransaction? DbTransaction { get; set; }

    public override int ExecuteNonQuery() => connection.ExecuteBatch(this, CancellationToken.None);

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return Task.FromResult(connection.ExecuteBatch(this, cancellationToken));
        }
        catch (Exception ex)
        {
            // Surface the scripted failure the way an async provider does: through the task.
            return Task.FromException<int>(ex);
        }
    }

    public override object? ExecuteScalar() => throw new NotSupportedException("The fake batch only executes non-queries.");

    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The fake batch only executes non-queries.");

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
        connection.ExecuteBatchReader(this, CancellationToken.None);

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        try
        {
            return Task.FromResult(connection.ExecuteBatchReader(this, cancellationToken));
        }
        catch (Exception ex)
        {
            // Surface the scripted failure the way an async provider does: through the task.
            return Task.FromException<DbDataReader>(ex);
        }
    }

    public override void Prepare() { }
    public override Task PrepareAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public override void Cancel() { }

    protected override DbBatchCommand CreateDbBatchCommand() => new FakeDbBatchCommand(connection);
}

/// <summary>Command of <see cref="FakeDbBatch"/>; parameters live in a real SQLite collection.</summary>
internal sealed class FakeDbBatchCommand(FakeBatchConnection connection) : DbBatchCommand
{
    private readonly SqliteCommand _parameterHost = new();
    private string _commandText = "";

    [AllowNull]
    public override string CommandText
    {
        get => _commandText;
        set => _commandText = value ?? "";
    }

    public override CommandType CommandType { get; set; }
    public override int RecordsAffected => 1;
    protected override DbParameterCollection DbParameterCollection => _parameterHost.Parameters;

    public override bool CanCreateParameter => connection.BatchCommandsCanCreateParameter;

    public override DbParameter CreateParameter()
    {
        connection.BatchParametersCreated++;
        return new SqliteParameter();
    }
}

/// <summary>Plain list of batch commands.</summary>
internal sealed class FakeDbBatchCommandCollection : DbBatchCommandCollection
{
    private readonly List<DbBatchCommand> _commands = [];

    public override int Count => _commands.Count;
    public override bool IsReadOnly => false;
    public override void Add(DbBatchCommand item) => _commands.Add(item);
    public override void Clear() => _commands.Clear();
    public override bool Contains(DbBatchCommand item) => _commands.Contains(item);
    public override void CopyTo(DbBatchCommand[] array, int arrayIndex) => _commands.CopyTo(array, arrayIndex);
    public override IEnumerator<DbBatchCommand> GetEnumerator() => _commands.GetEnumerator();
    public override int IndexOf(DbBatchCommand item) => _commands.IndexOf(item);
    public override void Insert(int index, DbBatchCommand item) => _commands.Insert(index, item);
    public override bool Remove(DbBatchCommand item) => _commands.Remove(item);
    public override void RemoveAt(int index) => _commands.RemoveAt(index);
    protected override DbBatchCommand GetBatchCommand(int index) => _commands[index];
    protected override void SetBatchCommand(int index, DbBatchCommand batchCommand) => _commands[index] = batchCommand;
}
