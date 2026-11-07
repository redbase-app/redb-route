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
/// How a poll consumer hands the polled rows to the route.
/// </summary>
public enum SqlPollDelivery
{
    /// <summary>An exchange per row; <c>onSuccess</c> / <c>onFailure</c> run for each row.</summary>
    PerRow,

    /// <summary>
    /// One exchange with every polled row (Apache Camel <c>useIterator=false</c>): a list, or with
    /// <see cref="SqlOutputType.StreamList"/> the open stream; <c>onSuccess</c> / <c>onFailure</c> run once for it.
    /// </summary>
    List
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

/// <summary>
/// How a <c>:#name</c> placeholder is written in the statement the connector sends to the provider.
/// </summary>
public enum SqlPlaceholderStyle
{
    /// <summary><c>@name</c>, one parameter per name: Npgsql, SqlClient, Microsoft.Data.Sqlite, MySqlConnector, Firebird.</summary>
    At,

    /// <summary><c>:name</c>, one parameter per occurrence, so binding by name and by position both work: Oracle (ODP.NET).</summary>
    Colon,

    /// <summary><c>?</c>, one parameter per occurrence, in order: ODBC and OleDb providers.</summary>
    Question
}
