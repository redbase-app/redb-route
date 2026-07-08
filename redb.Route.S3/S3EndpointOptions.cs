using redb.Route.Core;

namespace redb.Route.S3;

/// <summary>
/// Endpoint options for S3/MinIO transport. All properties are auto-bound from URI query parameters.
/// <para>
/// URI format: <c>s3://bucket-name?region=us-east-1&amp;accessKey=KEY&amp;secretKey=SECRET</c>
/// </para>
/// <para>
/// For producer operations: <c>s3:PutObject:bucket-name?keyName=folder/${header.name}&amp;...</c>
/// </para>
/// <para>
/// Enterprise-grade S3 integration compatible with AWS S3 and MinIO.
/// Supports multipart uploads, server-side encryption (SSE-S3, SSE-KMS, SSE-C),
/// presigned URLs, tagging, versioning, ACL, lifecycle, and more.
/// Inspired by Apache Camel's aws2-s3 component (81 parameters).
/// </para>
/// </summary>
public class S3EndpointOptions : EndpointOptions
{
    // ═══════════════════════════════════════════════════════════════════
    //  CONNECTION / CREDENTIALS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Custom service URL for S3-compatible storage (MinIO, Dell ECS, Ceph, etc.).
    /// Example: <c>http://localhost:9000</c> for MinIO.
    /// When set, <see cref="ForcePathStyle"/> should typically be <c>true</c>.
    /// </summary>
    public string ServiceUrl { get; set; } = "";

    /// <summary>
    /// AWS region (e.g. "us-east-1", "eu-west-1"). Required for AWS S3.
    /// For MinIO, use any value (e.g. "us-east-1"). (default: "us-east-1")
    /// </summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>AWS access key ID. Required unless using default credentials provider.</summary>
    public string AccessKey { get; set; } = "";

    /// <summary>AWS secret access key. Required unless using default credentials provider.</summary>
    public string SecretKey { get; set; } = "";

    /// <summary>AWS session token for temporary credentials (STS AssumeRole).</summary>
    public string SessionToken { get; set; } = "";

    /// <summary>AWS named profile for credentials (from ~/.aws/credentials).</summary>
    public string ProfileName { get; set; } = "";

    /// <summary>
    /// Use path-style URL (<c>http://host/bucket/key</c>) instead of virtual-hosted-style
    /// (<c>http://bucket.host/key</c>). Required for MinIO and most S3-compatible stores.
    /// (default: false)
    /// </summary>
    public bool ForcePathStyle { get; set; }

    /// <summary>
    /// Use the default AWS credentials provider chain (env vars, instance profile, etc.)
    /// instead of explicit AccessKey/SecretKey. (default: false)
    /// </summary>
    public bool UseDefaultCredentialsProvider { get; set; }

    /// <summary>
    /// Named connection factory reference. If set, looks up an <see cref="S3ConnectionFactory"/>
    /// from the DI registry by this name to build the S3 client. Overrides individual connection options.
    /// </summary>
    public string ConnectionFactory { get; set; } = "";

    // ── Proxy ────────────────────────────────────────────────────────

    /// <summary>HTTP proxy host for the S3 client.</summary>
    public string ProxyHost { get; set; } = "";

    /// <summary>HTTP proxy port. (default: 0 = no proxy)</summary>
    public int ProxyPort { get; set; }

    // ── Timeouts / Resilience ────────────────────────────────────────

    /// <summary>Connection timeout in milliseconds. (default: 30000 = 30s)</summary>
    public int ConnectionTimeout { get; set; } = 30_000;

    /// <summary>Socket/read timeout in milliseconds. (default: 60000 = 60s)</summary>
    public int SocketTimeout { get; set; } = 60_000;

    /// <summary>Maximum number of connections in the SDK connection pool. (default: 50)</summary>
    public int MaxConnections { get; set; } = 50;

    /// <summary>Number of retry attempts for failed requests. (default: 3)</summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>
    /// Retry mode: "standard" or "adaptive". (default: "standard")
    /// </summary>
    public string RetryMode { get; set; } = "standard";

    /// <summary>Trust all SSL certificates (for self-signed MinIO certs). (default: false)</summary>
    public bool TrustAllCertificates { get; set; }

    // ═══════════════════════════════════════════════════════════════════
    //  COMMON (consumer + producer)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Automatically create the S3 bucket if it doesn't exist.
    /// Also applies to <see cref="DestinationBucket"/> when <see cref="MoveAfterRead"/> is true.
    /// (default: false)
    /// </summary>
    public bool AutoCreateBucket { get; set; }

    /// <summary>
    /// Prefix filter for ListObjectsV2. Only objects whose key starts with this prefix
    /// are listed by the consumer or the ListObjects producer operation.
    /// </summary>
    public string Prefix { get; set; } = "";

    /// <summary>
    /// Delimiter for ListObjectsV2 to group keys (typically "/").
    /// When set, keys containing the delimiter after the prefix are rolled up into common-prefix entries.
    /// </summary>
    public string Delimiter { get; set; } = "";

    /// <summary>
    /// If true, download the object body and place it in the exchange body.
    /// If false, set headers only (metadata mode). (default: true)
    /// </summary>
    public bool IncludeBody { get; set; } = true;

    /// <summary>
    /// If true, the exchange body is set to the S3 response stream (Stream) instead of byte[].
    /// Takes precedence over <see cref="IncludeBody"/>. The stream is closed by <c>Exchange.DisposeAsync()</c>. Default: false.
    /// </summary>
    public bool StreamBody { get; set; }

    /// <summary>
    /// If true, the S3 object body is completely ignored (no download, no stream).
    /// Overrides <see cref="IncludeBody"/> and <see cref="StreamBody"/>. (default: false)
    /// </summary>
    public bool IgnoreBody { get; set; }

    /// <summary>
    /// If true, include folder/directory marker objects in consumer results.
    /// If false, objects ending with "/" are skipped. (default: false)
    /// </summary>
    public bool IncludeFolders { get; set; }

    // ═══════════════════════════════════════════════════════════════════
    //  CONSUMER (polling bucket)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Polling delay in milliseconds between bucket scans. (default: 60000 = 60s)</summary>
    public int Delay { get; set; } = 60_000;

    /// <summary>Initial delay before the first poll in milliseconds. (default: 1000)</summary>
    public int InitialDelay { get; set; } = 1000;

    /// <summary>Maximum number of objects to retrieve per poll. 0 = unlimited. (default: 10)</summary>
    public int MaxMessagesPerPoll { get; set; } = 10;

    /// <summary>
    /// Delete objects from S3 after they have been successfully consumed.
    /// Only performed if the exchange completes without error. (default: true)
    /// </summary>
    public bool DeleteAfterRead { get; set; } = true;

    /// <summary>
    /// Move objects to <see cref="DestinationBucket"/> after successful consumption
    /// instead of deleting. Requires <see cref="DestinationBucket"/> to be set.
    /// (default: false)
    /// </summary>
    public bool MoveAfterRead { get; set; }

    /// <summary>Destination bucket for move-after-read. Required when MoveAfterRead=true.</summary>
    public string DestinationBucket { get; set; } = "";

    /// <summary>Prefix to add to the destination key when moving objects.</summary>
    public string DestinationBucketPrefix { get; set; } = "";

    /// <summary>Suffix to add to the destination key when moving objects.</summary>
    public string DestinationBucketSuffix { get; set; } = "";

    /// <summary>
    /// Remove the source prefix from the key before constructing the destination key.
    /// Only applicable when <see cref="MoveAfterRead"/> is true. (default: false)
    /// </summary>
    public bool RemovePrefixOnMove { get; set; }

    /// <summary>
    /// Filter consumer to only process a specific object by key name.
    /// When set, the consumer downloads this single file directly (no listing).
    /// </summary>
    public string FileName { get; set; } = "";

    /// <summary>
    /// Glob pattern to include objects (applied to the object key). e.g. "*.csv", "data/*.json".
    /// Empty = all objects. (default: "")
    /// </summary>
    public string Include { get; set; } = "";

    /// <summary>
    /// Glob pattern to exclude objects. e.g. "*.tmp", ".hidden/*".
    /// Empty = exclude nothing. (default: "")
    /// </summary>
    public string Exclude { get; set; } = "";

    /// <summary>
    /// If the poll returns no objects, send an empty exchange (heartbeat).
    /// (default: false)
    /// </summary>
    public bool SendEmptyMessageWhenIdle { get; set; }

    /// <summary>
    /// Done file marker pattern. If set, the consumer only processes an object
    /// when a corresponding marker object exists (e.g. <c>${file:name}.done</c>).
    /// </summary>
    public string DoneFileName { get; set; } = "";

    /// <summary>Sort order for consumed objects. (default: None)</summary>
    public S3SortBy SortBy { get; set; } = S3SortBy.None;

    /// <summary>Minimum object age in milliseconds before consumption. (default: 0)</summary>
    public long MinAge { get; set; }

    /// <summary>Maximum object age in milliseconds. Objects older than this are skipped. 0 = no limit. (default: 0)</summary>
    public long MaxAge { get; set; }

    // ── Idempotency ─────────────────────────────────────────────────

    /// <summary>
    /// Enable idempotent consumer — skip objects already processed (tracked by key).
    /// Uses an in-memory repository by default. (default: false)
    /// </summary>
    public bool Idempotent { get; set; }

    /// <summary>
    /// Expression for the idempotent key. Default uses key + ETag + size.
    /// Supports <c>${header.xxx}</c> expressions.
    /// </summary>
    public string IdempotentKey { get; set; } = "";

    // ═══════════════════════════════════════════════════════════════════
    //  PRODUCER (uploading/operations)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Dynamic object key name for upload. Supports <c>${header.xxx}</c> expressions.
    /// If not set, falls back to the <see cref="S3Headers.Key"/> header from the exchange.
    /// </summary>
    public DynamicValue<string>? KeyName { get; set; }

    /// <summary>
    /// S3 storage class for uploaded objects: STANDARD, REDUCED_REDUNDANCY, GLACIER,
    /// STANDARD_IA, ONEZONE_IA, INTELLIGENT_TIERING, DEEP_ARCHIVE, GLACIER_IR.
    /// Empty = bucket default (STANDARD). (default: "")
    /// </summary>
    public string StorageClass { get; set; } = "";

    /// <summary>Content-Type for uploaded objects. Empty = auto-detect.</summary>
    public string ContentType { get; set; } = "";

    /// <summary>Content-Disposition header for uploaded objects.</summary>
    public string ContentDisposition { get; set; } = "";

    /// <summary>Content-Encoding header for uploaded objects (e.g. "gzip").</summary>
    public string ContentEncoding { get; set; } = "";

    /// <summary>Cache-Control header for uploaded objects.</summary>
    public string CacheControl { get; set; } = "";

    /// <summary>Canned ACL for uploaded objects. (default: Private)</summary>
    public S3CannedAcl CannedAcl { get; set; } = S3CannedAcl.Private;

    /// <summary>
    /// If true, use multipart upload for files larger than <see cref="PartSize"/>.
    /// Smaller files use single-part upload. (default: false)
    /// </summary>
    public bool MultiPartUpload { get; set; }

    /// <summary>
    /// Part size in bytes for multipart uploads. Minimum AWS limit: 5 MB.
    /// (default: 26214400 = 25 MB)
    /// </summary>
    public long PartSize { get; set; } = 26_214_400;

    /// <summary>Delete the source file/body after successful S3 upload. (default: false)</summary>
    public bool DeleteAfterWrite { get; set; }

    /// <summary>
    /// If true, upload only succeeds if the object key does not already exist.
    /// Uses conditional writes (x-amz-if-none-match). (default: false)
    /// </summary>
    public bool ConditionalWrite { get; set; }

    // ── Server-Side Encryption ──────────────────────────────────────

    /// <summary>Server-side encryption mode. (default: None)</summary>
    public S3ServerSideEncryption ServerSideEncryption { get; set; } = S3ServerSideEncryption.None;

    /// <summary>KMS key ID for SSE-KMS encryption.</summary>
    public string KmsKeyId { get; set; } = "";

    /// <summary>Customer encryption algorithm for SSE-C (e.g. "AES256").</summary>
    public string CustomerAlgorithm { get; set; } = "";

    /// <summary>Customer encryption key (base64-encoded) for SSE-C.</summary>
    public string CustomerKeyId { get; set; } = "";

    /// <summary>MD5 hash of the customer encryption key (base64-encoded) for SSE-C.</summary>
    public string CustomerKeyMD5 { get; set; } = "";

    // ── Presigned URLs ──────────────────────────────────────────────

    /// <summary>
    /// Presigned URL expiration in milliseconds.
    /// (default: 3600000 = 1 hour)
    /// </summary>
    public long PresignedUrlExpiration { get; set; } = 3_600_000;

    // ── Streaming Upload Mode ───────────────────────────────────────

    /// <summary>
    /// Enable streaming upload mode: accumulate messages and flush to S3
    /// as a single object. (default: false)
    /// </summary>
    public bool StreamingUploadMode { get; set; }

    /// <summary>Number of messages in a streaming upload batch before flush. (default: 10)</summary>
    public int BatchMessageNumber { get; set; } = 10;

    /// <summary>Maximum batch size in bytes for streaming upload. (default: 1048576 = 1 MB)</summary>
    public long BatchSize { get; set; } = 1_048_576;

    /// <summary>Buffer size in bytes for streaming upload. (default: 1048576 = 1 MB)</summary>
    public long BufferSize { get; set; } = 1_048_576;

    /// <summary>Timeout in milliseconds to flush a streaming batch. 0 = no timeout. (default: 0)</summary>
    public long StreamingUploadTimeout { get; set; }

    /// <summary>Naming strategy for streaming upload. (default: Progressive)</summary>
    public S3NamingStrategy NamingStrategy { get; set; } = S3NamingStrategy.Progressive;

    // ── Metadata ────────────────────────────────────────────────────

    /// <summary>
    /// User metadata as comma-separated key=value pairs to attach to uploaded objects.
    /// Example: <c>author=redb,env=production</c>.
    /// </summary>
    public string Metadata { get; set; } = "";

    // ═══════════════════════════════════════════════════════════════════
    //  VALIDATION
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public override void Validate()
    {
        if (!UseDefaultCredentialsProvider
            && string.IsNullOrEmpty(ConnectionFactory)
            && string.IsNullOrEmpty(ProfileName)
            && (string.IsNullOrEmpty(AccessKey) || string.IsNullOrEmpty(SecretKey)))
        {
            throw new ArgumentException(
                "S3 credentials required: provide AccessKey+SecretKey, set UseDefaultCredentialsProvider=true, " +
                "specify ProfileName, or reference a ConnectionFactory.");
        }

        if (string.IsNullOrWhiteSpace(Region))
            throw new ArgumentException("S3 Region must be specified.");

        if (Delay < 0)
            throw new ArgumentOutOfRangeException(nameof(Delay), Delay, "Delay cannot be negative.");

        if (InitialDelay < 0)
            throw new ArgumentOutOfRangeException(nameof(InitialDelay), InitialDelay, "InitialDelay cannot be negative.");

        if (MaxMessagesPerPoll < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxMessagesPerPoll), MaxMessagesPerPoll,
                "MaxMessagesPerPoll cannot be negative.");

        if (MoveAfterRead && string.IsNullOrEmpty(DestinationBucket))
            throw new InvalidOperationException("DestinationBucket is required when MoveAfterRead=true.");

        if (MoveAfterRead && DeleteAfterRead)
            throw new InvalidOperationException(
                "Cannot set both MoveAfterRead=true and DeleteAfterRead=true. MoveAfterRead implies the object is moved, not deleted from source after move.");

        if (MinAge < 0)
            throw new ArgumentOutOfRangeException(nameof(MinAge), MinAge, "MinAge cannot be negative.");

        if (MaxAge < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxAge), MaxAge, "MaxAge cannot be negative.");

        if (ConnectionTimeout < 0)
            throw new ArgumentOutOfRangeException(nameof(ConnectionTimeout), ConnectionTimeout,
                "ConnectionTimeout cannot be negative.");

        if (SocketTimeout < 0)
            throw new ArgumentOutOfRangeException(nameof(SocketTimeout), SocketTimeout,
                "SocketTimeout cannot be negative.");

        if (MaxConnections < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxConnections), MaxConnections,
                "MaxConnections must be at least 1.");

        if (RetryCount < 0)
            throw new ArgumentOutOfRangeException(nameof(RetryCount), RetryCount,
                "RetryCount cannot be negative.");

        if (PartSize < 5_242_880)
            throw new ArgumentOutOfRangeException(nameof(PartSize), PartSize,
                "PartSize must be at least 5 MB (5242880 bytes) for multipart upload.");

        if (PresignedUrlExpiration <= 0)
            throw new ArgumentOutOfRangeException(nameof(PresignedUrlExpiration), PresignedUrlExpiration,
                "PresignedUrlExpiration must be positive.");

        if (ServerSideEncryption == S3ServerSideEncryption.AwsKms && string.IsNullOrEmpty(KmsKeyId))
            throw new ArgumentException("KmsKeyId is required when ServerSideEncryption=AwsKms.");

        if (ServerSideEncryption == S3ServerSideEncryption.CustomerKey && string.IsNullOrEmpty(CustomerKeyId))
            throw new ArgumentException("CustomerKeyId is required when ServerSideEncryption=CustomerKey.");

        if (StreamingUploadMode && BatchMessageNumber < 1)
            throw new ArgumentOutOfRangeException(nameof(BatchMessageNumber), BatchMessageNumber,
                "BatchMessageNumber must be at least 1 in streaming upload mode.");

        if (StreamingUploadMode && BatchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(BatchSize), BatchSize,
                "BatchSize must be at least 1 in streaming upload mode.");

        if (ProxyPort is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(ProxyPort), ProxyPort,
                "ProxyPort must be 0–65535 (0 = no proxy).");
    }
}
