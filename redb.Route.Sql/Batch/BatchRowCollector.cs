using System.Collections;
using System.Data.Common;
using redb.Route.Sql.Mapping;

namespace redb.Route.Sql.Batch;

/// <summary>
/// Collects the rows a batch's statements return (<c>INSERT … RETURNING</c>, <c>OUTPUT inserted.*</c>) in item order, for
/// <see cref="SqlHeaders.GeneratedKeys"/>. Rows are <c>Dictionary&lt;string, object?&gt;</c>, or the <c>outputClass</c> type.
/// </summary>
internal sealed class BatchRowCollector
{
    private readonly Func<DbDataReader, object> _map;

    private BatchRowCollector(IList rows, Func<DbDataReader, object> map)
    {
        Rows = rows;
        _map = map;
    }

    /// <summary>The rows collected so far: a <c>List&lt;T&gt;</c> of the row type.</summary>
    internal IList Rows { get; }

    /// <summary>Number of rows collected so far.</summary>
    internal int Count => Rows.Count;

    /// <summary>
    /// A collector when <paramref name="options"/> ask for rows — an <c>outputType</c> other than <c>None</c>, with
    /// <c>Auto</c> decided by <paramref name="sql"/> — or null, when the batch reads no rows.
    /// </summary>
    internal static BatchRowCollector? Create(SqlEndpointOptions options, string sql)
    {
        // Under Auto a batch reads no rows, as Apache Camel's executeBatch collects no result sets: the keys of a batch are
        // asked for with an explicit outputType. The text is never parsed for it.
        if (options.OutputType is SqlOutputType.None or SqlOutputType.Auto)
            return null;

        if (SqlRowMapperFactory.Resolve(options.OutputClass) is { } poco)
            return new BatchRowCollector(poco.CreateList(), poco.Map);

        var mapper = new DictionaryRowMapper();
        return new BatchRowCollector(new List<Dictionary<string, object?>>(), reader => mapper.Map(reader));
    }

    /// <summary>Reads every row of every result set of <paramref name="reader"/>, in order.</summary>
    internal async Task ReadAllAsync(DbDataReader reader, CancellationToken ct)
    {
        do
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                Rows.Add(_map(reader));
        }
        while (await reader.NextResultAsync(ct).ConfigureAwait(false));
    }

    /// <summary>Drops the rows collected after the first <paramref name="count"/>: those of an item undone to its savepoint.</summary>
    internal void Truncate(int count)
    {
        while (Rows.Count > count)
            Rows.RemoveAt(Rows.Count - 1);
    }
}
