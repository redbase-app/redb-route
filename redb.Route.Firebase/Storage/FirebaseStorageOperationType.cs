namespace redb.Route.Firebase;

/// <summary>
/// Firebase Storage operation types for the producer.
/// Firebase Storage is GCS-compatible — uses <c>Google.Cloud.Storage.V1</c>.
/// </summary>
public enum FirebaseStorageOperationType
{
    /// <summary>Upload a file/byte[]/stream to Storage.</summary>
    Upload,

    /// <summary>Download an object to exchange body (byte[] or Stream).</summary>
    Download,

    /// <summary>Delete an object by name.</summary>
    Delete,

    /// <summary>List objects in the bucket with optional prefix filter.</summary>
    List,

    /// <summary>Get object metadata without downloading the body.</summary>
    GetMetadata
}
