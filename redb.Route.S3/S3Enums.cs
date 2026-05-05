namespace redb.Route.S3;

/// <summary>
/// Server-side encryption modes for S3 objects.
/// </summary>
public enum S3ServerSideEncryption
{
    /// <summary>No server-side encryption.</summary>
    None,

    /// <summary>SSE-S3: Amazon S3 managed keys (AES-256).</summary>
    Aes256,

    /// <summary>SSE-KMS: AWS Key Management Service managed keys.</summary>
    AwsKms,

    /// <summary>SSE-C: Customer-provided encryption keys.</summary>
    CustomerKey,
}

/// <summary>
/// Sort order for consumer polling results.
/// </summary>
public enum S3SortBy
{
    /// <summary>No sorting (S3 listing order).</summary>
    None,

    /// <summary>Sort by object key ascending.</summary>
    Key,

    /// <summary>Sort by object key descending.</summary>
    KeyDesc,

    /// <summary>Sort by last modified ascending.</summary>
    LastModified,

    /// <summary>Sort by last modified descending.</summary>
    LastModifiedDesc,

    /// <summary>Sort by size ascending.</summary>
    Size,

    /// <summary>Sort by size descending.</summary>
    SizeDesc,
}

/// <summary>
/// Naming strategy for streaming upload mode.
/// </summary>
public enum S3NamingStrategy
{
    /// <summary>Progressive numbering: key.txt, key-1.txt, key-2.txt, ...</summary>
    Progressive,

    /// <summary>Random UUID suffix: key-{guid}.txt</summary>
    Random,
}

/// <summary>
/// Canned ACL presets for S3 objects and buckets.
/// </summary>
public enum S3CannedAcl
{
    /// <summary>Owner gets FULL_CONTROL. No one else has access rights.</summary>
    Private,

    /// <summary>Owner gets FULL_CONTROL. The AllUsers group gets READ access.</summary>
    PublicRead,

    /// <summary>Owner gets FULL_CONTROL. The AllUsers group gets READ and WRITE access.</summary>
    PublicReadWrite,

    /// <summary>Owner gets FULL_CONTROL. The AuthenticatedUsers group gets READ access.</summary>
    AuthenticatedRead,

    /// <summary>Object owner gets FULL_CONTROL. Bucket owner gets READ access.</summary>
    BucketOwnerRead,

    /// <summary>Both the object owner and the bucket owner get FULL_CONTROL.</summary>
    BucketOwnerFullControl,
}
