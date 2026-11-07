using System.Data.Common;
using redb.Route.Abstractions;

namespace redb.Route.Sql.Batch;

/// <summary>What a batch writer works on: the open connection, the transaction (null inside an ambient one), the parameter
/// plan of the resolved statement, the reader over the items, the exchange that carries them, and the collector of the rows
/// the statements return (null when the batch reads no rows).</summary>
internal sealed record BatchWriteRequest(
    DbConnection Connection, DbTransaction? Transaction, SqlParameterPlan Plan, BatchItemReader Items, IExchange Exchange,
    BatchRowCollector? Keys);
