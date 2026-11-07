using System.Data;
using System.Data.Common;

namespace redb.Route.Tests.Sql.Batch.Fakes;

/// <summary>
/// Transaction of <see cref="FakeBatchConnection"/>; records how it ended and every savepoint call. Like SqlClient it
/// never claims <see cref="DbTransaction.SupportsSavepoints"/>; whether savepoints work is set on the connection.
/// </summary>
internal sealed class FakeBatchTransaction(FakeBatchConnection connection, IsolationLevel isolationLevel) : DbTransaction
{
    public bool Committed { get; private set; }
    public bool RolledBack { get; private set; }
    public bool IsDisposed { get; private set; }

    /// <summary>Savepoint calls in order: <c>save:name</c>, <c>release:name</c>, <c>rollback:name</c>.</summary>
    public List<string> SavepointLog { get; } = [];

    public override IsolationLevel IsolationLevel => isolationLevel;
    protected override DbConnection DbConnection => connection;

    public override void Commit() => Committed = true;

    public override void Rollback()
    {
        if (connection.FailRollback)
            throw new InvalidOperationException("fake: rollback failed, the server already ended the transaction");
        RolledBack = true;
    }

    public override void Save(string savepointName)
    {
        if (connection.Savepoints == SavepointBehavior.Unsupported)
            throw new NotSupportedException("fake: this provider has no savepoints");
        SavepointLog.Add("save:" + savepointName);
    }

    public override void Release(string savepointName)
    {
        if (connection.Savepoints == SavepointBehavior.Unsupported)
            throw new NotSupportedException("fake: this provider has no savepoints");
        SavepointLog.Add("release:" + savepointName);
    }

    public override void Rollback(string savepointName)
    {
        switch (connection.Savepoints)
        {
            case SavepointBehavior.Unsupported:
                throw new NotSupportedException("fake: this provider has no savepoints");
            case SavepointBehavior.RollbackToSavepointFails:
                SavepointLog.Add("rollback-failed:" + savepointName);
                throw new InvalidOperationException("fake: the server transaction has already ended");
            default:
                SavepointLog.Add("rollback:" + savepointName);
                break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        IsDisposed = true;
        base.Dispose(disposing);
    }
}
