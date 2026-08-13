using System.Text;
using redb.Route.Abstractions;

namespace redb.Route.S3;

/// <summary>
/// Fluent API for S3/MinIO endpoints.
/// <example><code>
/// // Consumer: poll bucket for new CSV files
/// .From(S3.Bucket("my-bucket")
///     .ServiceUrl("http://localhost:9000").ForcePathStyle()
///     .AccessKey("minioadmin").SecretKey("minioadmin")
///     .Prefix("incoming/").Include("*.csv")
///     .DeleteAfterRead().Delay(30_000))
///
/// // Producer: upload to S3 with SSE-KMS
/// .To(S3.Bucket("my-bucket")
///     .Region("eu-west-1").AccessKey("KEY").SecretKey("SECRET")
///     .KeyName(Expression.Header("fileName"))
///     .MultiPartUpload().PartSize(10_485_760)
///     .UseKmsEncryption("my-key-id")
///     .StorageClass("INTELLIGENT_TIERING"))
///
/// // Presigned URL generation
/// .To(S3.Bucket("my-bucket", S3OperationType.CreateDownloadLink)
///     .AccessKey("KEY").SecretKey("SECRET")
///     .PresignedUrlExpiration(7_200_000))
/// </code></example>
/// </summary>
public static class S3
{
    /// <summary>Creates an S3 endpoint for the given bucket name.</summary>
    public static S3Builder Bucket(string bucketName)
        => new(bucketName, null);

    /// <summary>Creates an S3 endpoint for a bucket with a specific producer operation.</summary>
    public static S3Builder Bucket(string bucketName, S3OperationType operation)
        => new(bucketName, operation);
}

/// <summary>
/// Fluent builder for S3 endpoint URIs. Maps to all <see cref="S3EndpointOptions"/> properties.
/// </summary>
public sealed class S3Builder
{
    private readonly string _bucketName;
    private readonly S3OperationType? _operation;

    // Connection / Credentials
    private string? _serviceUrl;
    private string? _region;
    private string? _accessKey;
    private string? _secretKey;
    private string? _sessionToken;
    private string? _profileName;
    private bool _forcePathStyle;
    private bool _useDefaultCredentialsProvider;
    private string? _connectionFactory;
    private string? _proxyHost;
    private string? _proxyPort;
    private string? _connectionTimeout;
    private string? _socketTimeout;
    private string? _maxConnections;
    private string? _retryCount;
    private string? _retryMode;
    private bool _trustAllCertificates;

    // Common
    private bool _autoCreateBucket;
    private string? _prefix;
    private string? _delimiter;
    private bool? _includeBody;
    private bool _streamBody;
    private bool _ignoreBody;
    private bool _includeFolders;

    // Consumer
    private string? _delay;
    private string? _initialDelay;
    private string? _maxMessagesPerPoll;
    private bool? _deleteAfterRead;
    private bool _moveAfterRead;
    private string? _destinationBucket;
    private string? _destinationBucketPrefix;
    private string? _destinationBucketSuffix;
    private bool _removePrefixOnMove;
    private string? _fileName;
    private string? _include;
    private string? _exclude;
    private bool _sendEmptyMessageWhenIdle;
    private string? _doneFileName;
    private string? _sortBy;
    private string? _minAge;
    private string? _maxAge;
    private bool _idempotent;
    private string? _idempotentKey;

    // Producer
    private string? _keyName;
    private string? _storageClass;
    private string? _contentType;
    private string? _contentDisposition;
    private string? _contentEncoding;
    private string? _cacheControl;
    private string? _cannedAcl;
    private bool _multiPartUpload;
    private string? _partSize;
    private bool _deleteAfterWrite;
    private bool _conditionalWrite;

    // SSE
    private string? _serverSideEncryption;
    private string? _kmsKeyId;
    private string? _customerAlgorithm;
    private string? _customerKeyId;
    private string? _customerKeyMD5;

    // Presigned
    private string? _presignedUrlExpiration;

    // Streaming Upload
    private bool _streamingUploadMode;
    private string? _batchMessageNumber;
    private string? _batchSize;
    private string? _bufferSize;
    private string? _streamingUploadTimeout;
    private string? _namingStrategy;

    // Metadata
    private string? _metadata;

    internal S3Builder(string bucketName, S3OperationType? operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);
        _bucketName = bucketName;
        _operation = operation;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CONNECTION / CREDENTIALS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Custom service URL for S3-compatible storage (MinIO, Ceph, etc.).</summary>
    public S3Builder ServiceUrl(string url) { _serviceUrl = url; return this; }
    /// <summary>Custom service URL from an expression.</summary>
    public S3Builder ServiceUrl(IExpression url) { _serviceUrl = url.ToTemplateString(); return this; }

    /// <summary>AWS region (e.g. "us-east-1"). Default "us-east-1".</summary>
    public S3Builder Region(string region) { _region = region; return this; }
    /// <summary>AWS region from an expression.</summary>
    public S3Builder Region(IExpression region) { _region = region.ToTemplateString(); return this; }

    /// <summary>AWS access key ID.</summary>
    public S3Builder AccessKey(string key) { _accessKey = key; return this; }
    /// <summary>AWS access key from an expression.</summary>
    public S3Builder AccessKey(IExpression key) { _accessKey = key.ToTemplateString(); return this; }

    /// <summary>AWS secret access key.</summary>
    public S3Builder SecretKey(string key) { _secretKey = key; return this; }
    /// <summary>AWS secret key from an expression.</summary>
    public S3Builder SecretKey(IExpression key) { _secretKey = key.ToTemplateString(); return this; }

    /// <summary>AWS session token for temporary credentials (STS).</summary>
    public S3Builder SessionToken(string token) { _sessionToken = token; return this; }
    /// <summary>Session token from an expression.</summary>
    public S3Builder SessionToken(IExpression token) { _sessionToken = token.ToTemplateString(); return this; }

    /// <summary>AWS named profile from ~/.aws/credentials.</summary>
    public S3Builder ProfileName(string profile) { _profileName = profile; return this; }

    /// <summary>Use path-style URLs. Required for MinIO and most S3-compatible stores.</summary>
    public S3Builder ForcePathStyle() { _forcePathStyle = true; return this; }

    /// <summary>Use the default AWS credentials provider chain.</summary>
    public S3Builder UseDefaultCredentials() { _useDefaultCredentialsProvider = true; return this; }

    /// <summary>Named connection factory reference from DI.</summary>
    public S3Builder ConnectionFactory(string name) { _connectionFactory = name; return this; }

    /// <summary>HTTP proxy configuration.</summary>
    public S3Builder Proxy(string host, int port)
    {
        _proxyHost = host; _proxyPort = port.ToString(); return this;
    }
    /// <summary>HTTP proxy with host from expression.</summary>
    public S3Builder Proxy(IExpression host, int port)
    {
        _proxyHost = host.ToTemplateString(); _proxyPort = port.ToString(); return this;
    }

    /// <summary>Connection timeout in milliseconds. Default 30000.</summary>
    public S3Builder ConnectionTimeout(int ms) { _connectionTimeout = ms.ToString(); return this; }
    /// <summary>Connection timeout from an expression.</summary>
    public S3Builder ConnectionTimeout(IExpression ms) { _connectionTimeout = ms.ToTemplateString(); return this; }

    /// <summary>Socket/read timeout in milliseconds. Default 60000.</summary>
    public S3Builder SocketTimeout(int ms) { _socketTimeout = ms.ToString(); return this; }
    /// <summary>Socket timeout from an expression.</summary>
    public S3Builder SocketTimeout(IExpression ms) { _socketTimeout = ms.ToTemplateString(); return this; }

    /// <summary>Max connections in the SDK pool. Default 50.</summary>
    public S3Builder MaxConnections(int count) { _maxConnections = count.ToString(); return this; }

    /// <summary>Retry count for failed requests. Default 3.</summary>
    public S3Builder RetryCount(int count) { _retryCount = count.ToString(); return this; }

    /// <summary>Retry mode: "standard" or "adaptive".</summary>
    public S3Builder RetryMode(string mode) { _retryMode = mode; return this; }

    /// <summary>Trust all SSL certificates (for self-signed certs).</summary>
    public S3Builder TrustAllCertificates() { _trustAllCertificates = true; return this; }

    // ═══════════════════════════════════════════════════════════════════
    //  COMMON
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Auto-create the bucket if it doesn't exist.</summary>
    public S3Builder AutoCreateBucket() { _autoCreateBucket = true; return this; }

    /// <summary>Prefix filter for object listing.</summary>
    public S3Builder Prefix(string prefix) { _prefix = prefix; return this; }

    /// <summary>Delimiter for ListObjectsV2 key grouping (typically "/").</summary>
    public S3Builder Delimiter(string delimiter) { _delimiter = delimiter; return this; }

    /// <summary>Download object body into exchange. Default true.</summary>
    public S3Builder IncludeBody(bool include = true) { _includeBody = include; return this; }

    /// <summary>Set exchange body to S3 response Stream instead of byte[].</summary>
    public S3Builder StreamBody() { _streamBody = true; return this; }

    /// <summary>Ignore object body completely (metadata mode).</summary>
    public S3Builder IgnoreBody() { _ignoreBody = true; return this; }

    /// <summary>Include folder/directory markers in consumer results.</summary>
    public S3Builder IncludeFolders() { _includeFolders = true; return this; }

    // ═══════════════════════════════════════════════════════════════════
    //  CONSUMER
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Polling delay in milliseconds between scans. Default 60000.</summary>
    public S3Builder Delay(int ms) { _delay = ms.ToString(); return this; }
    /// <summary>Polling delay from an expression.</summary>
    public S3Builder Delay(IExpression ms) { _delay = ms.ToTemplateString(); return this; }

    /// <summary>Initial delay before first poll in milliseconds. Default 1000.</summary>
    public S3Builder InitialDelay(int ms) { _initialDelay = ms.ToString(); return this; }
    /// <summary>Initial delay from an expression.</summary>
    public S3Builder InitialDelay(IExpression ms) { _initialDelay = ms.ToTemplateString(); return this; }

    /// <summary>Max objects per poll. 0 = unlimited. Default 10.</summary>
    public S3Builder MaxMessagesPerPoll(int max) { _maxMessagesPerPoll = max.ToString(); return this; }
    /// <summary>Max objects per poll from an expression.</summary>
    public S3Builder MaxMessagesPerPoll(IExpression max) { _maxMessagesPerPoll = max.ToTemplateString(); return this; }

    /// <summary>Delete objects from S3 after successful consumption. Default true.</summary>
    public S3Builder DeleteAfterRead(bool delete = true) { _deleteAfterRead = delete; return this; }

    /// <summary>Move objects to destination bucket after consumption instead of deleting.</summary>
    public S3Builder MoveAfterRead(string destinationBucket, string? prefix = null, string? suffix = null)
    {
        _moveAfterRead = true;
        _destinationBucket = destinationBucket;
        _destinationBucketPrefix = prefix;
        _destinationBucketSuffix = suffix;
        return this;
    }

    /// <summary>Remove source prefix from key when moving objects.</summary>
    public S3Builder RemovePrefixOnMove() { _removePrefixOnMove = true; return this; }

    /// <summary>Consume only a specific object by key name.</summary>
    public S3Builder FileName(string name) { _fileName = name; return this; }

    /// <summary>Glob pattern to include (e.g. "*.csv", "data/*.json").</summary>
    public S3Builder Include(string pattern) { _include = pattern; return this; }

    /// <summary>Glob pattern to exclude (e.g. "*.tmp").</summary>
    public S3Builder Exclude(string pattern) { _exclude = pattern; return this; }

    /// <summary>Send empty exchange when poll returns no objects (heartbeat).</summary>
    public S3Builder SendEmptyMessageWhenIdle() { _sendEmptyMessageWhenIdle = true; return this; }

    /// <summary>Done file marker pattern (e.g. "${file:name}.done").</summary>
    public S3Builder DoneFileName(string pattern) { _doneFileName = pattern; return this; }
    /// <summary>Done file marker from an expression.</summary>
    public S3Builder DoneFileName(IExpression pattern) { _doneFileName = pattern.ToTemplateString(); return this; }

    /// <summary>Sort order: None, Key, KeyDesc, LastModified, LastModifiedDesc, Size, SizeDesc.</summary>
    public S3Builder SortBy(S3SortBy sort) { _sortBy = sort.ToString(); return this; }

    /// <summary>Minimum object age in milliseconds before consumption.</summary>
    public S3Builder MinAge(long ms) { _minAge = ms.ToString(); return this; }
    /// <summary>Minimum object age from an expression.</summary>
    public S3Builder MinAge(IExpression ms) { _minAge = ms.ToTemplateString(); return this; }

    /// <summary>Maximum object age in milliseconds. 0 = no limit.</summary>
    public S3Builder MaxAge(long ms) { _maxAge = ms.ToString(); return this; }
    /// <summary>Maximum object age from an expression.</summary>
    public S3Builder MaxAge(IExpression ms) { _maxAge = ms.ToTemplateString(); return this; }

    /// <summary>Enable idempotent consumer with optional key expression.</summary>
    public S3Builder Idempotent(IExpression? key = null)
    {
        _idempotent = true; _idempotentKey = key?.ToTemplateString(); return this;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PRODUCER
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Dynamic object key name for upload. Supports expressions.</summary>
    public S3Builder KeyName(string name) { _keyName = name; return this; }
    /// <summary>Dynamic object key from an expression (e.g. Header("fileName")).</summary>
    public S3Builder KeyName(IExpression name) { _keyName = name.ToTemplateString(); return this; }

    /// <summary>S3 storage class (STANDARD, GLACIER, INTELLIGENT_TIERING, etc.).</summary>
    public S3Builder StorageClass(string storageClass) { _storageClass = storageClass; return this; }

    /// <summary>Content-Type for uploaded objects. Empty = auto-detect.</summary>
    public S3Builder ContentType(string contentType) { _contentType = contentType; return this; }

    /// <summary>Content-Disposition for uploaded objects.</summary>
    public S3Builder ContentDisposition(string disposition) { _contentDisposition = disposition; return this; }

    /// <summary>Content-Encoding for uploaded objects (e.g. "gzip").</summary>
    public S3Builder ContentEncoding(string encoding) { _contentEncoding = encoding; return this; }

    /// <summary>Cache-Control header for uploaded objects.</summary>
    public S3Builder CacheControl(string cacheControl) { _cacheControl = cacheControl; return this; }

    /// <summary>Canned ACL for uploaded objects.</summary>
    public S3Builder CannedAcl(S3CannedAcl acl) { _cannedAcl = acl.ToString(); return this; }

    /// <summary>Enable multipart upload for large files.</summary>
    public S3Builder MultiPartUpload() { _multiPartUpload = true; return this; }

    /// <summary>Part size in bytes for multipart upload. Minimum 5 MB. Default 25 MB.</summary>
    public S3Builder PartSize(long bytes) { _partSize = bytes.ToString(); return this; }

    /// <summary>Delete source after successful S3 upload.</summary>
    public S3Builder DeleteAfterWrite() { _deleteAfterWrite = true; return this; }

    /// <summary>Conditional write — fail if object already exists.</summary>
    public S3Builder ConditionalWrite() { _conditionalWrite = true; return this; }

    // ═══════════════════════════════════════════════════════════════════
    //  ENCRYPTION (convenience methods)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Enable SSE-S3 encryption (AES-256, managed by AWS).</summary>
    public S3Builder UseAes256Encryption()
    {
        _serverSideEncryption = S3ServerSideEncryption.Aes256.ToString(); return this;
    }

    /// <summary>Enable SSE-KMS encryption with the given AWS KMS key ID.</summary>
    public S3Builder UseKmsEncryption(string kmsKeyId)
    {
        _serverSideEncryption = S3ServerSideEncryption.AwsKms.ToString();
        _kmsKeyId = kmsKeyId;
        return this;
    }

    /// <summary>Enable SSE-C encryption with customer-provided key material.</summary>
    public S3Builder UseCustomerEncryption(string algorithm, string keyId, string? keyMD5 = null)
    {
        _serverSideEncryption = S3ServerSideEncryption.CustomerKey.ToString();
        _customerAlgorithm = algorithm;
        _customerKeyId = keyId;
        _customerKeyMD5 = keyMD5;
        return this;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PRESIGNED URL
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Presigned URL expiration in milliseconds. Default 3600000 (1h).</summary>
    public S3Builder PresignedUrlExpiration(long ms) { _presignedUrlExpiration = ms.ToString(); return this; }

    // ═══════════════════════════════════════════════════════════════════
    //  STREAMING UPLOAD
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Enable streaming upload mode — accumulate and flush.</summary>
    public S3Builder StreamingUpload(int batchMessages = 10, long batchSize = 1_048_576)
    {
        _streamingUploadMode = true;
        _batchMessageNumber = batchMessages.ToString();
        _batchSize = batchSize.ToString();
        return this;
    }

    /// <summary>Buffer size for streaming upload. Default 1 MB.</summary>
    public S3Builder BufferSize(long bytes) { _bufferSize = bytes.ToString(); return this; }

    /// <summary>Timeout to flush a streaming batch. 0 = no timeout.</summary>
    public S3Builder StreamingUploadTimeout(long ms) { _streamingUploadTimeout = ms.ToString(); return this; }

    /// <summary>Naming strategy for streaming upload keys.</summary>
    public S3Builder NamingStrategy(S3NamingStrategy strategy) { _namingStrategy = strategy.ToString(); return this; }

    // ═══════════════════════════════════════════════════════════════════
    //  METADATA
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>User metadata as comma-separated key=value pairs (e.g. "author=redb,env=prod").</summary>
    public S3Builder Metadata(string kvPairs) { _metadata = kvPairs; return this; }

    // ═══════════════════════════════════════════════════════════════════
    //  BUILD
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Builds the S3 endpoint URI string.</summary>
    public string Build()
    {
        var sb = new StringBuilder();
        sb.Append("s3://");

        // Operation prefix: "s3://PutObject:bucket-name"
        if (_operation.HasValue)
        {
            sb.Append(_operation.Value);
            sb.Append(':');
        }
        sb.Append(_bucketName);

        var sep = '?';

        void Append(string key, string value)
        {
            sb.Append(sep); sb.Append(key); sb.Append('=');
            sb.Append(Uri.EscapeDataString(value)); sep = '&';
        }

        void AppendIf(string key, string? value) { if (!string.IsNullOrEmpty(value)) Append(key, value); }
        void AppendBool(string key, bool value) { if (value) Append(key, "true"); }
        void AppendBoolExplicit(string key, bool? value)
        {
            if (value.HasValue) Append(key, value.Value.ToString().ToLowerInvariant());
        }

        // Connection / Credentials
        AppendIf("serviceUrl", _serviceUrl);
        AppendIf("region", _region);
        AppendIf("accessKey", _accessKey);
        AppendIf("secretKey", _secretKey);
        AppendIf("sessionToken", _sessionToken);
        AppendIf("profileName", _profileName);
        AppendBool("forcePathStyle", _forcePathStyle);
        AppendBool("useDefaultCredentialsProvider", _useDefaultCredentialsProvider);
        AppendIf("connectionFactory", _connectionFactory);
        AppendIf("proxyHost", _proxyHost);
        AppendIf("proxyPort", _proxyPort);
        AppendIf("connectionTimeout", _connectionTimeout);
        AppendIf("socketTimeout", _socketTimeout);
        AppendIf("maxConnections", _maxConnections);
        AppendIf("retryCount", _retryCount);
        AppendIf("retryMode", _retryMode);
        AppendBool("trustAllCertificates", _trustAllCertificates);

        // Common
        AppendBool("autoCreateBucket", _autoCreateBucket);
        AppendIf("prefix", _prefix);
        AppendIf("delimiter", _delimiter);
        AppendBoolExplicit("includeBody", _includeBody);
        AppendBool("streamBody", _streamBody);
        AppendBool("ignoreBody", _ignoreBody);
        AppendBool("includeFolders", _includeFolders);

        // Consumer
        AppendIf("delay", _delay);
        AppendIf("initialDelay", _initialDelay);
        AppendIf("maxMessagesPerPoll", _maxMessagesPerPoll);
        AppendBoolExplicit("deleteAfterRead", _deleteAfterRead);
        AppendBool("moveAfterRead", _moveAfterRead);
        AppendIf("destinationBucket", _destinationBucket);
        AppendIf("destinationBucketPrefix", _destinationBucketPrefix);
        AppendIf("destinationBucketSuffix", _destinationBucketSuffix);
        AppendBool("removePrefixOnMove", _removePrefixOnMove);
        AppendIf("fileName", _fileName);
        AppendIf("include", _include);
        AppendIf("exclude", _exclude);
        AppendBool("sendEmptyMessageWhenIdle", _sendEmptyMessageWhenIdle);
        AppendIf("doneFileName", _doneFileName);
        AppendIf("sortBy", _sortBy);
        AppendIf("minAge", _minAge);
        AppendIf("maxAge", _maxAge);
        AppendBool("idempotent", _idempotent);
        AppendIf("idempotentKey", _idempotentKey);

        // Producer
        AppendIf("keyName", _keyName);
        AppendIf("storageClass", _storageClass);
        AppendIf("contentType", _contentType);
        AppendIf("contentDisposition", _contentDisposition);
        AppendIf("contentEncoding", _contentEncoding);
        AppendIf("cacheControl", _cacheControl);
        AppendIf("cannedAcl", _cannedAcl);
        AppendBool("multiPartUpload", _multiPartUpload);
        AppendIf("partSize", _partSize);
        AppendBool("deleteAfterWrite", _deleteAfterWrite);
        AppendBool("conditionalWrite", _conditionalWrite);

        // SSE
        AppendIf("serverSideEncryption", _serverSideEncryption);
        AppendIf("kmsKeyId", _kmsKeyId);
        AppendIf("customerAlgorithm", _customerAlgorithm);
        AppendIf("customerKeyId", _customerKeyId);
        AppendIf("customerKeyMD5", _customerKeyMD5);

        // Presigned
        AppendIf("presignedUrlExpiration", _presignedUrlExpiration);

        // Streaming Upload
        AppendBool("streamingUploadMode", _streamingUploadMode);
        AppendIf("batchMessageNumber", _batchMessageNumber);
        AppendIf("batchSize", _batchSize);
        AppendIf("bufferSize", _bufferSize);
        AppendIf("streamingUploadTimeout", _streamingUploadTimeout);
        AppendIf("namingStrategy", _namingStrategy);

        // Metadata
        AppendIf("metadata", _metadata);

        return sb.ToString();
    }

    /// <summary>Implicit conversion to URI string.</summary>
    public static implicit operator string(S3Builder b) => b.Build();

    /// <inheritdoc/>
    public override string ToString() => Build();
}
