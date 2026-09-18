using System.Data.Common;

namespace redb.Route.Tests.Sql.Batch.Fakes;

/// <summary>A provider error raised by the fake connection's script; may name the batch command that failed.</summary>
internal sealed class FakeDbException(string message, DbBatchCommand? batchCommand = null) : DbException(message)
{
    protected override DbBatchCommand? DbBatchCommand => batchCommand;
}
