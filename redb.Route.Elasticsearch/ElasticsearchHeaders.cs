namespace redb.Route.Elasticsearch;

/// <summary>
/// Well-known header constants used by the Elasticsearch component.
/// Follows the <c>redbEs.</c> prefix convention.
/// </summary>
public static class ElasticsearchHeaders
{
    /// <summary>Common prefix for all Elasticsearch component headers.</summary>
    public const string Prefix = "redbEs.";

    // ═══════════════════════════════════════════════════════════════════
    //  COMMON (set by consumer, read by producer)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Index name.</summary>
    public const string IndexName = "redbEs.IndexName";

    /// <summary>Document ID (_id).</summary>
    public const string DocumentId = "redbEs.DocumentId";

    /// <summary>Document version (_version).</summary>
    public const string Version = "redbEs.Version";

    /// <summary>Sequence number (_seq_no) for optimistic concurrency.</summary>
    public const string SequenceNumber = "redbEs.SequenceNumber";

    /// <summary>Primary term (_primary_term) for optimistic concurrency.</summary>
    public const string PrimaryTerm = "redbEs.PrimaryTerm";

    /// <summary>Operation to perform (used to override endpoint operation).</summary>
    public const string Operation = "redbEs.Operation";

    // ═══════════════════════════════════════════════════════════════════
    //  INDEX / UPDATE / DELETE RESULT
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Result of the operation: created, updated, deleted, noop.</summary>
    public const string Result = "redbEs.Result";

    // ═══════════════════════════════════════════════════════════════════
    //  SEARCH (consumer & producer)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Relevance score (_score).</summary>
    public const string Score = "redbEs.Score";

    /// <summary>Total number of matching documents.</summary>
    public const string TotalHits = "redbEs.TotalHits";

    /// <summary>Total hits relation: "eq" or "gte".</summary>
    public const string TotalHitsRelation = "redbEs.TotalHitsRelation";

    /// <summary>Scroll context ID for scroll API.</summary>
    public const string ScrollId = "redbEs.ScrollId";

    /// <summary>Sort values from the last hit (for search_after pagination).</summary>
    public const string SortValues = "redbEs.SortValues";

    // ═══════════════════════════════════════════════════════════════════
    //  BULK
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Number of items in the bulk response.</summary>
    public const string BulkItemCount = "redbEs.BulkItemCount";

    /// <summary>Whether the bulk response had errors (bool).</summary>
    public const string BulkErrors = "redbEs.BulkErrors";

    /// <summary>Serialized error details from failed bulk items (JSON).</summary>
    public const string BulkErrorItems = "redbEs.BulkErrorItems";

    // ═══════════════════════════════════════════════════════════════════
    //  MULTISEARCH
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Number of individual search responses returned.</summary>
    public const string MultiSearchResponseCount = "redbEs.MultiSearchResponseCount";

    /// <summary>Whether any sub-query returned an error (bool).</summary>
    public const string MultiSearchHasErrors = "redbEs.MultiSearchHasErrors";

    /// <summary>Array of total hits per sub-query (long[]).</summary>
    public const string MultiSearchTotalHits = "redbEs.MultiSearchTotalHits";

    // ═══════════════════════════════════════════════════════════════════
    //  REQUEST ROUTING
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Ingest pipeline name override.</summary>
    public const string Pipeline = "redbEs.Pipeline";

    /// <summary>Custom routing value override.</summary>
    public const string Routing = "redbEs.Routing";

    /// <summary>Refresh policy override: "true", "false", or "wait_for".</summary>
    public const string Refresh = "redbEs.Refresh";

    /// <summary>Returns true if the header key starts with the ES prefix.</summary>
    public static bool IsRedbHeader(string headerKey) =>
        headerKey.StartsWith(Prefix, StringComparison.Ordinal);
}
