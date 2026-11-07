namespace redb.Route.S3;

/// <summary>
/// Well-known header constants used by the S3 component.
/// Follows the <c>redbS3.</c> prefix convention.
/// </summary>
public static class S3Headers
{
    /// <summary>Common prefix for all S3 component headers.</summary>
    public const string Prefix = "redbS3.";

    // ═══════════════════════════════════════════════════════════════════
    //  COMMON (set by consumer, read by producer)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Bucket name.</summary>
    public const string BucketName = "redbS3.BucketName";

    /// <summary>Object key (path within the bucket).</summary>
    public const string Key = "redbS3.Key";

    /// <summary>Content-Type MIME type of the object.</summary>
    public const string ContentType = "redbS3.ContentType";

    /// <summary>Content-Length in bytes.</summary>
    public const string ContentLength = "redbS3.ContentLength";

    /// <summary>Content-MD5 (base64-encoded).</summary>
    public const string ContentMD5 = "redbS3.ContentMD5";

    /// <summary>Content-Disposition header value.</summary>
    public const string ContentDisposition = "redbS3.ContentDisposition";

    /// <summary>Content-Encoding header value.</summary>
    public const string ContentEncoding = "redbS3.ContentEncoding";

    /// <summary>Cache-Control header value.</summary>
    public const string CacheControl = "redbS3.CacheControl";

    /// <summary>ETag of the object (hex-encoded MD5 on upload, or opaque for multipart).</summary>
    public const string ETag = "redbS3.ETag";

    /// <summary>Last modified timestamp.</summary>
    public const string LastModified = "redbS3.LastModified";

    /// <summary>S3 storage class (STANDARD, GLACIER, etc.).</summary>
    public const string StorageClass = "redbS3.StorageClass";

    /// <summary>Version ID (if bucket versioning is enabled).</summary>
    public const string VersionId = "redbS3.VersionId";

    /// <summary>Server-side encryption algorithm.</summary>
    public const string ServerSideEncryption = "redbS3.ServerSideEncryption";

    /// <summary>S3 operation to perform (used to override endpoint operation).</summary>
    public const string Operation = "redbS3.Operation";

    // ═══════════════════════════════════════════════════════════════════
    //  CONSUMER (set on incoming messages)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Object expiration time (if lifecycle configured).</summary>
    public const string ExpirationTime = "redbS3.ExpirationTime";

    /// <summary>Replication status (COMPLETE, PENDING, FAILED, REPLICA).</summary>
    public const string ReplicationStatus = "redbS3.ReplicationStatus";

    /// <summary>Message timestamp (Unix ms).</summary>
    public const string Timestamp = "redbS3.Timestamp";

    // ═══════════════════════════════════════════════════════════════════
    //  PRODUCER INPUT (set by user before sending)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Destination bucket name (for CopyObject).</summary>
    public const string DestinationBucket = "redbS3.DestinationBucket";

    /// <summary>Destination object key (for CopyObject).</summary>
    public const string DestinationKey = "redbS3.DestinationKey";

    /// <summary>Canned ACL preset (Private, PublicRead, etc.).</summary>
    public const string CannedAcl = "redbS3.CannedAcl";

    /// <summary>Range start byte offset (for GetObjectRange).</summary>
    public const string RangeStart = "redbS3.RangeStart";

    /// <summary>Range end byte offset (for GetObjectRange).</summary>
    public const string RangeEnd = "redbS3.RangeEnd";

    /// <summary>Presigned URL expiration in milliseconds.</summary>
    public const string PresignedUrlExpiration = "redbS3.PresignedUrlExpiration";

    /// <summary>List of keys to delete (for DeleteObjects operation).</summary>
    public const string KeysToDelete = "redbS3.KeysToDelete";

    /// <summary>Object tags dictionary (for PutObjectTagging).</summary>
    public const string ObjectTags = "redbS3.ObjectTags";

    /// <summary>Versioning status: "Enabled" or "Suspended" (for PutBucketVersioning).</summary>
    public const string VersioningStatus = "redbS3.VersioningStatus";

    /// <summary>Number of days for Glacier restore (for RestoreObject).</summary>
    public const string RestoreDays = "redbS3.RestoreDays";

    /// <summary>Restore tier: Standard, Bulk, Expedited (for RestoreObject).</summary>
    public const string RestoreTier = "redbS3.RestoreTier";

    /// <summary>Override bucket name dynamically (overrides endpoint bucket).</summary>
    public const string OverrideBucketName = "redbS3.OverrideBucketName";

    // ═══════════════════════════════════════════════════════════════════
    //  METADATA
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Prefix for user metadata headers. Example: <c>redbS3.Meta.x-amz-meta-author</c>.
    /// All headers with this prefix are mapped to S3 object metadata on upload
    /// and populated from S3 object metadata on download.
    /// </summary>
    public const string MetadataPrefix = "redbS3.Meta.";

    // ═══════════════════════════════════════════════════════════════════
    //  PRODUCER OUTPUT
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Presigned URL (set after CreateDownloadLink/CreateUploadLink).</summary>
    public const string PresignedUrl = "redbS3.PresignedUrl";

    /// <summary>The produced (resolved) key name after upload.</summary>
    public const string ProducedKey = "redbS3.ProducedKey";

    /// <summary>The produced (resolved) bucket name after operation.</summary>
    public const string ProducedBucketName = "redbS3.ProducedBucketName";

    /// <summary>Whether the bucket exists (set after HeadBucket).</summary>
    public const string BucketExists = "redbS3.BucketExists";

    /// <summary>Returns true if the header key belongs to the S3 component.</summary>
    public static bool IsRedbHeader(string key) =>
        key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
}
