namespace redb.Route.Tests.Sql.Batch.Fakes;

/// <summary>How the fake transaction answers savepoint calls.</summary>
internal enum SavepointBehavior
{
    /// <summary>The <see cref="System.Data.Common.DbTransaction"/> base behaviour: <c>Save</c> throws <see cref="NotSupportedException"/>.</summary>
    Unsupported,

    /// <summary>Save, release and rollback-to-savepoint succeed.</summary>
    Works,

    /// <summary>Save succeeds; rolling back to the savepoint fails — the server already ended the transaction.</summary>
    RollbackToSavepointFails,
}
