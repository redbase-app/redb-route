using System.Text;
using redb.Route.Abstractions;

namespace redb.Route.S3;

/// <summary>Consumer, producer, encryption, presigned-URL and streaming-upload verbs of <see cref="S3Builder"/>.</summary>
public sealed partial class S3Builder
{
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

    /// <summary>Enable idempotent consumer (key = object Key + ETag + Size).</summary>
    public S3Builder Idempotent() { _idempotent = true; return this; }

    /// <summary>Named IIdempotentRepository from the context registry (survives restarts).</summary>
    public S3Builder IdempotentRepository(string name) { _idempotentRepository = name; return this; }

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

    // Streaming Upload verbs removed in часть B of the options sweep: the mode was never
    // implemented, and accumulate-then-write belongs to the core Aggregate(...) EIP.

    // ═══════════════════════════════════════════════════════════════════
    //  METADATA
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>User metadata as comma-separated key=value pairs (e.g. "author=redb,env=prod").</summary>
    public S3Builder Metadata(string kvPairs) { _metadata = kvPairs; return this; }
}
