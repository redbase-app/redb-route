namespace redb.Route.Sql;

/// <summary>
/// Operating mode for the SQL endpoint.
/// Determines whether the endpoint acts as a polling consumer or a producer.
/// </summary>
public enum SqlMode
{
    /// <summary>Polling consumer: periodically executes SELECT and emits an Exchange per row.</summary>
    Poll,

    /// <summary>Producer: executes INSERT/UPDATE/DELETE/SELECT statements.</summary>
    Execute,

    /// <summary>Producer: calls a stored procedure or function with IN/OUT/INOUT parameters.</summary>
    Procedure
}

/// <summary>
/// Determines how SQL query results are mapped to the Exchange body.
/// </summary>
public enum SqlOutputType
{
    /// <summary>
    /// Auto-detect based on the SQL statement: SELECT/WITH → <see cref="SelectList"/>,
    /// INSERT/UPDATE/DELETE/MERGE → <see cref="None"/>.
    /// </summary>
    Auto,

    /// <summary>All rows → <c>List&lt;Dictionary&lt;string, object&gt;&gt;</c>.</summary>
    SelectList,

    /// <summary>First row only → <c>Dictionary&lt;string, object&gt;</c> (or mapped POCO).</summary>
    SelectOne,

    /// <summary>Streaming: <c>IAsyncEnumerable&lt;Dictionary&lt;string, object&gt;&gt;</c>.</summary>
    StreamList,

    /// <summary>First column of first row (scalar value).</summary>
    Scalar,

    /// <summary>No result body. Affected rows count goes to header <c>redbSql.updateCount</c>.</summary>
    None
}

/// <summary>
/// Direction of a stored procedure parameter.
/// </summary>
public enum SqlParamDirection
{
    /// <summary>Input-only parameter.</summary>
    In,

    /// <summary>Output-only parameter (value set by the procedure).</summary>
    Out,

    /// <summary>Bi-directional parameter (input and output).</summary>
    InOut
}
