namespace redb.Route.S3;

/// <summary>
/// S3 operation types — determines what the producer does.
/// Parsed from the first path segment of the URI: <c>s3:OPERATION:bucket</c>.
/// When no operation is specified, the default is <see cref="PutObject"/> for producer
/// and polling list for consumer.
/// </summary>
public enum S3OperationType
{
    // ── Object operations ──

    /// <summary>Upload an object to the bucket. Default producer operation.</summary>
    PutObject,

    /// <summary>Download an object from the bucket.</summary>
    GetObject,

    /// <summary>Download a byte range of an object.</summary>
    GetObjectRange,

    /// <summary>Delete a single object.</summary>
    DeleteObject,

    /// <summary>Delete multiple objects in a single request.</summary>
    DeleteObjects,

    /// <summary>Copy an object from one bucket/key to another.</summary>
    CopyObject,

    /// <summary>List objects in the bucket (with optional prefix/delimiter).</summary>
    ListObjects,

    /// <summary>Get object metadata without downloading the body.</summary>
    HeadObject,

    // ── Presigned URLs ──

    /// <summary>Generate a presigned download URL.</summary>
    CreateDownloadLink,

    /// <summary>Generate a presigned upload URL.</summary>
    CreateUploadLink,

    // ── Bucket operations ──

    /// <summary>Create a new bucket.</summary>
    CreateBucket,

    /// <summary>Delete a bucket.</summary>
    DeleteBucket,

    /// <summary>Check if a bucket exists and is accessible.</summary>
    HeadBucket,

    /// <summary>List all buckets for the account.</summary>
    ListBuckets,

    // ── Tagging ──

    /// <summary>Get tags on an object.</summary>
    GetObjectTagging,

    /// <summary>Set tags on an object.</summary>
    PutObjectTagging,

    /// <summary>Delete all tags from an object.</summary>
    DeleteObjectTagging,

    // ── ACL ──

    /// <summary>Get the ACL of an object.</summary>
    GetObjectAcl,

    /// <summary>Set the ACL of an object.</summary>
    PutObjectAcl,

    // ── Versioning ──

    /// <summary>Get bucket versioning configuration.</summary>
    GetBucketVersioning,

    /// <summary>Set bucket versioning (enable/suspend).</summary>
    PutBucketVersioning,

    // ── Restore (Glacier) ──

    /// <summary>Restore an archived (Glacier) object.</summary>
    RestoreObject,
}
