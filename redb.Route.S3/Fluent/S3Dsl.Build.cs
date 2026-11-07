using System.Text;
using redb.Route.Abstractions;

namespace redb.Route.S3;

/// <summary>URI assembly of <see cref="S3Builder"/>.</summary>
public sealed partial class S3Builder
{

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
        AppendIf("idempotentRepository", _idempotentRepository);

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

        // Metadata
        AppendIf("metadata", _metadata);

        return sb.ToString();
    }

    /// <summary>Implicit conversion to URI string.</summary>
    public static implicit operator string(S3Builder b) => b.Build();

    /// <inheritdoc/>
    public override string ToString() => Build();
}
