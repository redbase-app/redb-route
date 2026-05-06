namespace redb.Route.Elasticsearch;

/// <summary>
/// Elasticsearch operation types — determines what the producer does.
/// Parsed from the first path segment of the URI: <c>elasticsearch:OPERATION:index-name</c>
/// or <c>es:OPERATION:index-name</c>.
/// When no operation is specified, the default is <see cref="Index"/> for producer.
/// </summary>
public enum ElasticsearchOperationType
{
    /// <summary>Index (upsert) a single document. Default producer operation.</summary>
    Index,

    /// <summary>Bulk API — batch index/update/delete operations.</summary>
    Bulk,

    /// <summary>Search API — returns hits (InOut pattern).</summary>
    Search,

    /// <summary>Get a single document by ID (InOut pattern).</summary>
    Get,

    /// <summary>Partial update a document by ID.</summary>
    Update,

    /// <summary>Delete a single document by ID.</summary>
    Delete,

    /// <summary>Count documents matching a query (InOut pattern).</summary>
    Count,

    /// <summary>Check if a document exists by ID (InOut pattern).</summary>
    Exists,

    /// <summary>Multi-search API — execute multiple search queries in a single request (InOut pattern).</summary>
    MultiSearch,
}
