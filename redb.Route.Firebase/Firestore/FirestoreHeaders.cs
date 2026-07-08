namespace redb.Route.Firebase;

/// <summary>
/// Header constants for the Firestore connector. Prefix: <c>redbFirestore.</c>
/// </summary>
public static class FirestoreHeaders
{
    /// <summary>Common prefix for all Firestore headers.</summary>
    public const string Prefix = "redbFirestore.";

    // ── Document identity ──

    /// <summary>Document ID within the collection.</summary>
    public const string DocumentId = "redbFirestore.DocumentId";

    /// <summary>Full document path (e.g. <c>users/user-123</c>).</summary>
    public const string DocumentPath = "redbFirestore.DocumentPath";

    /// <summary>Collection path (e.g. <c>users</c>, <c>users/uid/orders</c>).</summary>
    public const string CollectionPath = "redbFirestore.CollectionPath";

    // ── Metadata ──

    /// <summary>Document creation timestamp.</summary>
    public const string CreateTime = "redbFirestore.CreateTime";

    /// <summary>Last update timestamp.</summary>
    public const string UpdateTime = "redbFirestore.UpdateTime";

    /// <summary>Read timestamp (snapshot read time).</summary>
    public const string ReadTime = "redbFirestore.ReadTime";

    // ── Consumer: Change tracking ──

    /// <summary>Type of change: "Added", "Modified", or "Removed".</summary>
    public const string ChangeType = "redbFirestore.ChangeType";

    // ── Producer results ──

    /// <summary>Write result timestamp (from Firestore server).</summary>
    public const string WriteTime = "redbFirestore.WriteTime";

    // ── Query info ──

    /// <summary>Number of documents returned by a query or batch operation.</summary>
    public const string DocumentCount = "redbFirestore.DocumentCount";
}
