using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace redb.Route.Tests.Sql.Batch.Fakes;

/// <summary>
/// Command of <see cref="FakeBatchConnection"/>: executions go to the connection's script. Parameters live in a real
/// SQLite parameter collection, so the connector's binding code runs unchanged.
/// </summary>
internal sealed class FakeBatchCommand : DbCommand
{
    private readonly FakeBatchConnection _connection;
    private readonly SqliteCommand _parameterHost = new();
    private string _commandText = "";

    public FakeBatchCommand(FakeBatchConnection connection)
    {
        _connection = connection;
        DbConnection = connection;
    }

    [AllowNull]
    public override string CommandText
    {
        get => _commandText;
        set => _commandText = value ?? "";
    }

    public override int CommandTimeout { get; set; }
    public override CommandType CommandType { get; set; }
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection? DbConnection { get; set; }
    protected override DbParameterCollection DbParameterCollection => _parameterHost.Parameters;
    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel() { }
    public override void Prepare() { }

    public override int ExecuteNonQuery() => _connection.Execute(CommandText, CancellationToken.None);

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        try
        {
            return Task.FromResult(_connection.Execute(CommandText, cancellationToken));
        }
        catch (Exception ex)
        {
            // Surface the scripted failure the way an async provider does: through the task.
            return Task.FromException<int>(ex);
        }
    }

    public override object? ExecuteScalar() => _connection.Execute(CommandText, CancellationToken.None);

    protected override DbParameter CreateDbParameter() => new SqliteParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
        throw new NotSupportedException("The fake batch connection only executes non-queries.");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _parameterHost.Dispose();
        base.Dispose(disposing);
    }
}
