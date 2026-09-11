using System.Diagnostics;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.S3;

/// <summary>
/// S3 producer — sends messages to S3. Extends <see cref="ConnectableProducer"/>
/// for persistent SDK client lifecycle.
/// <para>
/// Supports 22 operations: PutObject, GetObject, GetObjectRange, DeleteObject,
/// DeleteObjects, CopyObject, ListObjects, HeadObject, CreateDownloadLink,
/// CreateUploadLink, CreateBucket, DeleteBucket, HeadBucket, ListBuckets,
/// Get/Put/DeleteObjectTagging, Get/PutObjectAcl, Get/PutBucketVersioning, RestoreObject.
/// </para>
/// </summary>
internal sealed partial class S3Producer : ConnectableProducer
{
    private readonly S3Endpoint _endpoint;
    private readonly S3EndpointOptions _options;
    private IAmazonS3? _client;

    /// <inheritdoc />
    protected override IEndpoint ProducerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ProducerName => $"s3:{_endpoint.OperationType}:{_endpoint.BucketName}";

    internal S3Producer(S3Endpoint endpoint, S3EndpointOptions options)
    {
        _endpoint = endpoint;
        _options = options;
    }

    /// <inheritdoc />
    protected override async Task ConnectAsync(CancellationToken ct)
    {
        _client = await _endpoint.GetOrCreateClientAsync(ct).ConfigureAwait(false);

        if (_options.AutoCreateBucket)
            await EnsureBucketExistsAsync(_endpoint.BucketName, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        EnsureStarted();
        ArgumentNullException.ThrowIfNull(exchange);

        // Allow runtime operation override via header
        var operation = _endpoint.OperationType;
        if (exchange.In.Headers.TryGetValue(S3Headers.Operation, out var opHeader) && opHeader is string opStr)
        {
            if (Enum.TryParse<S3OperationType>(opStr, ignoreCase: true, out var headerOp))
                operation = headerOp;
        }

        using var activity = RouteTelemetryExtensions.StartTransportSpan(
            $"s3 {operation}", ActivityKind.Client,
            "redb.system", "s3",
            _endpoint.Uri.NormalizedKey,
            destination: _endpoint.BucketName,
            operation: operation.ToString());

        await DispatchOperationAsync(operation, exchange, ct).ConfigureAwait(false);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  OPERATION DISPATCH
    // ═══════════════════════════════════════════════════════════════════

    private async Task DispatchOperationAsync(S3OperationType operation, IExchange exchange, CancellationToken ct)
    {
        switch (operation)
        {
            case S3OperationType.PutObject:
                await ProcessPutObjectAsync(exchange, ct).ConfigureAwait(false);
                break;
            case S3OperationType.GetObject:
                await ProcessGetObjectAsync(exchange, ct).ConfigureAwait(false);
                break;
            case S3OperationType.GetObjectRange:
                await ProcessGetObjectRangeAsync(exchange, ct).ConfigureAwait(false);
                break;
            case S3OperationType.DeleteObject:
                await ProcessDeleteObjectAsync(exchange, ct).ConfigureAwait(false);
                break;
            case S3OperationType.DeleteObjects:
                await ProcessDeleteObjectsAsync(exchange, ct).ConfigureAwait(false);
                break;
            case S3OperationType.CopyObject:
                await ProcessCopyObjectAsync(exchange, ct).ConfigureAwait(false);
                break;
            case S3OperationType.ListObjects:
                await ProcessListObjectsAsync(exchange, ct).ConfigureAwait(false);
                break;
            case S3OperationType.HeadObject:
                await ProcessHeadObjectAsync(exchange, ct).ConfigureAwait(false);
                break;
            case S3OperationType.CreateDownloadLink:
                await ProcessCreateDownloadLinkAsync(exchange, ct).ConfigureAwait(false);
                break;
            case S3OperationType.CreateUploadLink:
                await ProcessCreateUploadLinkAsync(exchange, ct).ConfigureAwait(false);
                break;
            case S3OperationType.CreateBucket:
                await ProcessCreateBucketAsync(exchange, ct).ConfigureAwait(false);
                break;
            case S3OperationType.DeleteBucket:
                await ProcessDeleteBucketAsync(exchange, ct).ConfigureAwait(false);
                break;
            case S3OperationType.HeadBucket:
                await ProcessHeadBucketAsync(exchange, ct).ConfigureAwait(false);
                break;
            case S3OperationType.ListBuckets:
                await ProcessListBucketsAsync(exchange, ct).ConfigureAwait(false);
                break;
            case S3OperationType.GetObjectTagging:
                await ProcessGetObjectTaggingAsync(exchange, ct).ConfigureAwait(false);
                break;
            case S3OperationType.PutObjectTagging:
                await ProcessPutObjectTaggingAsync(exchange, ct).ConfigureAwait(false);
                break;
            case S3OperationType.DeleteObjectTagging:
                await ProcessDeleteObjectTaggingAsync(exchange, ct).ConfigureAwait(false);
                break;
            case S3OperationType.GetObjectAcl:
                await ProcessGetObjectAclAsync(exchange, ct).ConfigureAwait(false);
                break;
            case S3OperationType.PutObjectAcl:
                await ProcessPutObjectAclAsync(exchange, ct).ConfigureAwait(false);
                break;
            case S3OperationType.GetBucketVersioning:
                await ProcessGetBucketVersioningAsync(exchange, ct).ConfigureAwait(false);
                break;
            case S3OperationType.PutBucketVersioning:
                await ProcessPutBucketVersioningAsync(exchange, ct).ConfigureAwait(false);
                break;
            case S3OperationType.RestoreObject:
                await ProcessRestoreObjectAsync(exchange, ct).ConfigureAwait(false);
                break;
            default:
                throw new NotSupportedException($"S3 operation '{operation}' is not supported.");
        }

        // MessagesOut is recorded by the core (ToProcessor / the template) - ownership audit.
    }

    // ═══════════════════════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════════════════════

    private string ResolveBucket(IExchange exchange)
    {
        // Priority: header override → endpoint bucket
        return exchange.In.GetHeader<string>(S3Headers.OverrideBucketName)
               ?? _endpoint.BucketName;
    }

    private string ResolveKey(IExchange exchange)
    {
        // Priority: KeyName option (dynamic) → header → fallback to empty
        if (_options.KeyName is not null)
        {
            var resolved = _options.KeyName.Value.Resolve(exchange);
            if (!string.IsNullOrEmpty(resolved))
                return resolved;
        }

        return exchange.In.GetHeader<string>(S3Headers.Key)
               ?? throw new InvalidOperationException(
                   $"Object key must be provided via KeyName option or '{S3Headers.Key}' header.");
    }

    private void ApplyContentHeaders(PutObjectRequest request, IExchange exchange)
    {
        var contentType = exchange.In.GetHeader<string>(S3Headers.ContentType) ?? _options.ContentType;
        if (!string.IsNullOrEmpty(contentType))
            request.ContentType = contentType;

        var disposition = exchange.In.GetHeader<string>(S3Headers.ContentDisposition) ?? _options.ContentDisposition;
        if (!string.IsNullOrEmpty(disposition))
            request.Headers["Content-Disposition"] = disposition;

        var encoding = exchange.In.GetHeader<string>(S3Headers.ContentEncoding) ?? _options.ContentEncoding;
        if (!string.IsNullOrEmpty(encoding))
            request.Headers["Content-Encoding"] = encoding;

        var cacheControl = exchange.In.GetHeader<string>(S3Headers.CacheControl) ?? _options.CacheControl;
        if (!string.IsNullOrEmpty(cacheControl))
            request.Headers["Cache-Control"] = cacheControl;
    }

    private void ApplyEncryption(PutObjectRequest request)
    {
        switch (_options.ServerSideEncryption)
        {
            case S3ServerSideEncryption.Aes256:
                request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256;
                break;
            case S3ServerSideEncryption.AwsKms:
                request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS;
                if (!string.IsNullOrEmpty(_options.KmsKeyId))
                    request.ServerSideEncryptionKeyManagementServiceKeyId = _options.KmsKeyId;
                break;
            case S3ServerSideEncryption.CustomerKey:
                request.ServerSideEncryptionCustomerMethod = ServerSideEncryptionCustomerMethod.AES256;
                request.ServerSideEncryptionCustomerProvidedKey = _options.CustomerKeyId;
                if (!string.IsNullOrEmpty(_options.CustomerKeyMD5))
                    request.ServerSideEncryptionCustomerProvidedKeyMD5 = _options.CustomerKeyMD5;
                break;
        }
    }

    private void ApplyMetadata(PutObjectRequest request, IExchange exchange)
    {
        // From options
        if (!string.IsNullOrEmpty(_options.Metadata))
        {
            foreach (var pair in _options.Metadata.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eqIndex = pair.IndexOf('=');
                if (eqIndex > 0)
                    request.Metadata.Add(pair[..eqIndex].Trim(), pair[(eqIndex + 1)..].Trim());
            }
        }

        // From headers (MetadataPrefix)
        foreach (var header in exchange.In.Headers)
        {
            if (header.Key.StartsWith(S3Headers.MetadataPrefix, StringComparison.OrdinalIgnoreCase) && header.Value is string value)
            {
                var metaKey = header.Key[S3Headers.MetadataPrefix.Length..];
                request.Metadata.Add(metaKey, value);
            }
        }
    }

    private void ApplyStorageClass(PutObjectRequest request)
    {
        if (!string.IsNullOrEmpty(_options.StorageClass))
            request.StorageClass = new S3StorageClass(_options.StorageClass);
    }

    private void ApplyCannedAcl(PutObjectRequest request, IExchange exchange)
    {
        var cannedAclStr = exchange.In.GetHeader<string>(S3Headers.CannedAcl);
        if (!string.IsNullOrEmpty(cannedAclStr))
        {
            request.CannedACL = MapCannedAcl(cannedAclStr);
            return;
        }

        if (_options.CannedAcl != S3CannedAcl.Private)
            request.CannedACL = MapCannedAcl(_options.CannedAcl.ToString());
    }

    private static S3CannedACL MapCannedAcl(string value) => value switch
    {
        "Private" or "private" => S3CannedACL.Private,
        "PublicRead" or "public-read" => S3CannedACL.PublicRead,
        "PublicReadWrite" or "public-read-write" => S3CannedACL.PublicReadWrite,
        "AuthenticatedRead" or "authenticated-read" => S3CannedACL.AuthenticatedRead,
        "BucketOwnerRead" or "bucket-owner-read" => S3CannedACL.BucketOwnerRead,
        "BucketOwnerFullControl" or "bucket-owner-full-control" => S3CannedACL.BucketOwnerFullControl,
        _ => S3CannedACL.Private,
    };

    private void SetProducedHeaders(IExchange exchange, string bucket, string key, string? etag, string? versionId)
    {
        exchange.In.Headers[S3Headers.ProducedBucketName] = bucket;
        exchange.In.Headers[S3Headers.ProducedKey] = key;
        if (!string.IsNullOrEmpty(etag))
            exchange.In.Headers[S3Headers.ETag] = etag;
        if (!string.IsNullOrEmpty(versionId))
            exchange.In.Headers[S3Headers.VersionId] = versionId;
    }

    private static void SetObjectMetadataHeaders(IExchange exchange, GetObjectResponse response)
    {
        exchange.In.Headers[S3Headers.BucketName] = response.BucketName;
        exchange.In.Headers[S3Headers.Key] = response.Key;
        exchange.In.Headers[S3Headers.ContentType] = response.Headers.ContentType;
        exchange.In.Headers[S3Headers.ContentLength] = response.ContentLength;
        exchange.In.Headers[S3Headers.ETag] = response.ETag;
        exchange.In.Headers[S3Headers.LastModified] = response.LastModified;
        exchange.In.Headers[S3Headers.VersionId] = response.VersionId;
        exchange.In.Headers[S3Headers.StorageClass] = response.StorageClass?.Value;
        exchange.In.Headers[S3Headers.ServerSideEncryption] = response.ServerSideEncryptionMethod?.Value;

        // User metadata
        foreach (var meta in response.Metadata.Keys)
            exchange.In.Headers[$"{S3Headers.MetadataPrefix}{meta}"] = response.Metadata[meta];
    }

    private async Task EnsureBucketExistsAsync(string bucket, CancellationToken ct)
    {
        try
        {
            await _client!.EnsureBucketExistsAsync(bucket).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            // Bucket already exists (race condition) — ignore
        }
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer, totalRead, buffer.Length - totalRead, ct).ConfigureAwait(false);
            if (read == 0)
                break;
            totalRead += read;
        }
        return totalRead;
    }
}

/// <summary>
/// Lightweight object info DTO for ListObjects results.
/// </summary>
public sealed class S3ObjectInfo
{
    /// <summary>Object key.</summary>
    public string Key { get; set; } = "";

    /// <summary>Object size in bytes.</summary>
    public long Size { get; set; }

    /// <summary>Last modified timestamp.</summary>
    public DateTime LastModified { get; set; }

    /// <summary>ETag of the object.</summary>
    public string ETag { get; set; } = "";

    /// <summary>Storage class.</summary>
    public string? StorageClass { get; set; }
}
