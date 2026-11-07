namespace redb.Route.Sql;

/// <summary>
/// A batch item that failed in a batch configured with <c>breakBatchOnError=false</c>: its statement was undone to the
/// item's savepoint and the batch went on. The failed items of a batch are carried in the
/// <see cref="SqlHeaders.BatchErrors"/> header.
/// </summary>
/// <param name="Index">Zero-based position of the item in the batch source.</param>
/// <param name="Message">The provider's error message.</param>
/// <param name="SqlState">
/// The SQLSTATE code when the provider reports one (<see cref="System.Data.Common.DbException.SqlState"/>), otherwise null.
/// </param>
public sealed record SqlBatchItemError(int Index, string Message, string? SqlState);
