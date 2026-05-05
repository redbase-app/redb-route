namespace redb.Route.Firebase;

/// <summary>
/// Firestore operation types for the producer.
/// Determines what CRUD action the producer performs.
/// </summary>
public enum FirestoreOperationType
{
    /// <summary>Create or overwrite a document (uses SetAsync).</summary>
    Set,

    /// <summary>Read a single document by ID.</summary>
    Get,

    /// <summary>Partial update of a document (merge fields).</summary>
    Update,

    /// <summary>Delete a document by ID.</summary>
    Delete,

    /// <summary>Query a collection with filters, ordering, and limits.</summary>
    Query,

    /// <summary>Batch write multiple documents in a single commit.</summary>
    BatchWrite
}
