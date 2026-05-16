using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

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
internal sealed class S3Producer : ConnectableProducer
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

        _endpoint.RecordMessageOut();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  OBJECT OPERATIONS
    // ═══════════════════════════════════════════════════════════════════

    private async Task ProcessPutObjectAsync(IExchange exchange, CancellationToken ct)
    {
        var bucket = ResolveBucket(exchange);
        var key = ResolveKey(exchange);

        var request = new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
        };

        // Resolve body to stream
        var body = exchange.In.Body;
        switch (body)
        {
            case Stream stream:
                request.InputStream = stream;
                break;
            case byte[] bytes:
                request.InputStream = new MemoryStream(bytes);
                break;
            case string text:
                request.ContentBody = text;
                break;
            case null:
                request.InputStream = new MemoryStream(Array.Empty<byte>());
                break;
            default:
                request.ContentBody = body.ToString();
                break;
        }

        // Content headers
        ApplyContentHeaders(request, exchange);
        ApplyEncryption(request);
        ApplyMetadata(request, exchange);
        ApplyStorageClass(request);
        ApplyCannedAcl(request, exchange);

        if (_options.MultiPartUpload && request.InputStream is { Length: > 0 } s && s.Length > _options.PartSize)
        {
            await MultiPartUploadAsync(bucket, key, request, ct).ConfigureAwait(false);
        }
        else
        {
            var response = await _client!.PutObjectAsync(request, ct).ConfigureAwait(false);
            SetProducedHeaders(exchange, bucket, key, response.ETag, response.VersionId);
        }

        if (request.InputStream is MemoryStream ms && body is not Stream)
            ms.Dispose();

        Logger?.LogDebug("S3 PutObject: {Bucket}/{Key}", bucket, key);
    }

    private async Task MultiPartUploadAsync(string bucket, string key, PutObjectRequest request, CancellationToken ct)
    {
        var initRequest = new InitiateMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            ContentType = request.ContentType,
            StorageClass = request.StorageClass,
        };

        // Copy SSE settings
        initRequest.ServerSideEncryptionMethod = request.ServerSideEncryptionMethod;
        if (!string.IsNullOrEmpty(request.ServerSideEncryptionKeyManagementServiceKeyId))
            initRequest.ServerSideEncryptionKeyManagementServiceKeyId = request.ServerSideEncryptionKeyManagementServiceKeyId;

        var initResponse = await _client!.InitiateMultipartUploadAsync(initRequest, ct).ConfigureAwait(false);
        var uploadId = initResponse.UploadId;

        try
        {
            var partETags = new List<PartETag>();
            var stream = request.InputStream!;
            stream.Position = 0;
            var partNumber = 1;
            var buffer = new byte[_options.PartSize];

            while (true)
            {
                var bytesRead = await ReadExactAsync(stream, buffer, ct).ConfigureAwait(false);
                if (bytesRead == 0)
                    break;

                using var partStream = new MemoryStream(buffer, 0, bytesRead);
                var uploadPartRequest = new UploadPartRequest
                {
                    BucketName = bucket,
                    Key = key,
                    UploadId = uploadId,
                    PartNumber = partNumber,
                    InputStream = partStream,
                };

                var uploadPartResponse = await _client.UploadPartAsync(uploadPartRequest, ct).ConfigureAwait(false);
                partETags.Add(new PartETag(partNumber, uploadPartResponse.ETag));
                partNumber++;
            }

            var completeRequest = new CompleteMultipartUploadRequest
            {
                BucketName = bucket,
                Key = key,
                UploadId = uploadId,
                PartETags = partETags,
            };

            await _client.CompleteMultipartUploadAsync(completeRequest, ct).ConfigureAwait(false);
            Logger?.LogDebug("S3 MultiPartUpload completed: {Bucket}/{Key}, {Parts} parts", bucket, key, partETags.Count);
        }
        catch
        {
            // Abort multipart upload on failure
            try
            {
                await _client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                {
                    BucketName = bucket,
                    Key = key,
                    UploadId = uploadId,
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception abortEx)
            {
                Logger?.LogWarning(abortEx, "Failed to abort multipart upload {UploadId}", uploadId);
            }
            throw;
        }
    }

    private async Task ProcessGetObjectAsync(IExchange exchange, CancellationToken ct)
    {
        var bucket = ResolveBucket(exchange);
        var key = ResolveKey(exchange);

        var request = new GetObjectRequest
        {
            BucketName = bucket,
            Key = key,
        };

        var response = await _client!.GetObjectAsync(request, ct).ConfigureAwait(false);

        if (_options.IgnoreBody)
        {
            response.ResponseStream?.Dispose();
        }
        else if (_options.StreamBody)
        {
            exchange.In.Body = response.ResponseStream;
        }
        else if (_options.IncludeBody)
        {
            using var ms = new MemoryStream();
            await response.ResponseStream.CopyToAsync(ms, ct).ConfigureAwait(false);
            exchange.In.Body = ms.ToArray();
            response.ResponseStream.Dispose();
            _endpoint.RecordBytesIn(ms.Length);
        }
        else
        {
            exchange.In.Body = response.ResponseStream;
        }

        SetObjectMetadataHeaders(exchange, response);
    }

    private async Task ProcessGetObjectRangeAsync(IExchange exchange, CancellationToken ct)
    {
        var bucket = ResolveBucket(exchange);
        var key = ResolveKey(exchange);

        var rangeStart = exchange.In.GetHeader<long?>(S3Headers.RangeStart) ?? 0;
        var rangeEnd = exchange.In.GetHeader<long?>(S3Headers.RangeEnd) ?? 0;

        var request = new GetObjectRequest
        {
            BucketName = bucket,
            Key = key,
            ByteRange = new ByteRange(rangeStart, rangeEnd),
        };

        var response = await _client!.GetObjectAsync(request, ct).ConfigureAwait(false);

        using var ms = new MemoryStream();
        await response.ResponseStream.CopyToAsync(ms, ct).ConfigureAwait(false);
        exchange.In.Body = ms.ToArray();
        response.ResponseStream.Dispose();

        SetObjectMetadataHeaders(exchange, response);
        _endpoint.RecordBytesIn(ms.Length);
    }

    private async Task ProcessDeleteObjectAsync(IExchange exchange, CancellationToken ct)
    {
        var bucket = ResolveBucket(exchange);
        var key = ResolveKey(exchange);

        await _client!.DeleteObjectAsync(bucket, key, ct).ConfigureAwait(false);
        Logger?.LogDebug("S3 DeleteObject: {Bucket}/{Key}", bucket, key);
    }

    private async Task ProcessDeleteObjectsAsync(IExchange exchange, CancellationToken ct)
    {
        var bucket = ResolveBucket(exchange);

        var keys = exchange.In.GetHeader<List<string>>(S3Headers.KeysToDelete)
                   ?? throw new InvalidOperationException($"Header '{S3Headers.KeysToDelete}' (List<string>) is required for DeleteObjects.");

        var request = new DeleteObjectsRequest
        {
            BucketName = bucket,
            Objects = keys.Select(k => new KeyVersion { Key = k }).ToList(),
        };

        var response = await _client!.DeleteObjectsAsync(request, ct).ConfigureAwait(false);
        exchange.In.Body = response.DeletedObjects.Select(d => d.Key).ToList();
        Logger?.LogDebug("S3 DeleteObjects: {Bucket}, {Count} deleted", bucket, response.DeletedObjects.Count);
    }

    private async Task ProcessCopyObjectAsync(IExchange exchange, CancellationToken ct)
    {
        var sourceBucket = ResolveBucket(exchange);
        var sourceKey = ResolveKey(exchange);

        var destBucket = exchange.In.GetHeader<string>(S3Headers.DestinationBucket)
                         ?? throw new InvalidOperationException($"Header '{S3Headers.DestinationBucket}' is required for CopyObject.");
        var destKey = exchange.In.GetHeader<string>(S3Headers.DestinationKey)
                      ?? sourceKey;

        var request = new CopyObjectRequest
        {
            SourceBucket = sourceBucket,
            SourceKey = sourceKey,
            DestinationBucket = destBucket,
            DestinationKey = destKey,
        };

        var response = await _client!.CopyObjectAsync(request, ct).ConfigureAwait(false);

        exchange.In.Headers[S3Headers.ETag] = response.ETag;
        exchange.In.Headers[S3Headers.VersionId] = response.VersionId;
        Logger?.LogDebug("S3 CopyObject: {SrcBucket}/{SrcKey} → {DstBucket}/{DstKey}", sourceBucket, sourceKey, destBucket, destKey);
    }

    private async Task ProcessListObjectsAsync(IExchange exchange, CancellationToken ct)
    {
        var bucket = ResolveBucket(exchange);

        var request = new ListObjectsV2Request
        {
            BucketName = bucket,
            Prefix = _options.Prefix,
            Delimiter = _options.Delimiter,
            MaxKeys = _options.MaxMessagesPerPoll > 0 ? _options.MaxMessagesPerPoll : 1000,
        };

        var response = await _client!.ListObjectsV2Async(request, ct).ConfigureAwait(false);
        exchange.In.Body = response.S3Objects.Select(o => new S3ObjectInfo
        {
            Key = o.Key,
            Size = o.Size ?? 0,
            LastModified = o.LastModified ?? DateTime.MinValue,
            ETag = o.ETag,
            StorageClass = o.StorageClass?.Value,
        }).ToList();

        Logger?.LogDebug("S3 ListObjects: {Bucket}, prefix={Prefix}, {Count} objects", bucket, _options.Prefix, response.S3Objects.Count);
    }

    private async Task ProcessHeadObjectAsync(IExchange exchange, CancellationToken ct)
    {
        var bucket = ResolveBucket(exchange);
        var key = ResolveKey(exchange);

        var request = new GetObjectMetadataRequest
        {
            BucketName = bucket,
            Key = key,
        };

        var response = await _client!.GetObjectMetadataAsync(request, ct).ConfigureAwait(false);

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

    // ═══════════════════════════════════════════════════════════════════
    //  PRESIGNED URLS
    // ═══════════════════════════════════════════════════════════════════

    private Task ProcessCreateDownloadLinkAsync(IExchange exchange, CancellationToken ct)
    {
        var bucket = ResolveBucket(exchange);
        var key = ResolveKey(exchange);

        var expirationMs = exchange.In.GetHeader<long?>(S3Headers.PresignedUrlExpiration)
                           ?? _options.PresignedUrlExpiration;

        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = key,
            Expires = DateTime.UtcNow.AddMilliseconds(expirationMs),
            Verb = HttpVerb.GET,
        };

        var url = _client!.GetPreSignedURL(request);
        exchange.In.Headers[S3Headers.PresignedUrl] = url;
        exchange.In.Body = url;

        Logger?.LogDebug("S3 CreateDownloadLink: {Bucket}/{Key}, expires={ExpirationMs}ms", bucket, key, expirationMs);
        return Task.CompletedTask;
    }

    private Task ProcessCreateUploadLinkAsync(IExchange exchange, CancellationToken ct)
    {
        var bucket = ResolveBucket(exchange);
        var key = ResolveKey(exchange);

        var expirationMs = exchange.In.GetHeader<long?>(S3Headers.PresignedUrlExpiration)
                           ?? _options.PresignedUrlExpiration;

        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = key,
            Expires = DateTime.UtcNow.AddMilliseconds(expirationMs),
            Verb = HttpVerb.PUT,
        };

        if (!string.IsNullOrEmpty(_options.ContentType))
            request.ContentType = _options.ContentType;

        var url = _client!.GetPreSignedURL(request);
        exchange.In.Headers[S3Headers.PresignedUrl] = url;
        exchange.In.Body = url;

        Logger?.LogDebug("S3 CreateUploadLink: {Bucket}/{Key}, expires={ExpirationMs}ms", bucket, key, expirationMs);
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  BUCKET OPERATIONS
    // ═══════════════════════════════════════════════════════════════════

    private async Task ProcessCreateBucketAsync(IExchange exchange, CancellationToken ct)
    {
        var bucket = ResolveBucket(exchange);
        await EnsureBucketExistsAsync(bucket, ct).ConfigureAwait(false);
        Logger?.LogDebug("S3 CreateBucket: {Bucket}", bucket);
    }

    private async Task ProcessDeleteBucketAsync(IExchange exchange, CancellationToken ct)
    {
        var bucket = ResolveBucket(exchange);
        await _client!.DeleteBucketAsync(bucket, ct).ConfigureAwait(false);
        Logger?.LogDebug("S3 DeleteBucket: {Bucket}", bucket);
    }

    private async Task ProcessHeadBucketAsync(IExchange exchange, CancellationToken ct)
    {
        var bucket = ResolveBucket(exchange);
        bool exists;
        try
        {
            await _client!.EnsureBucketExistsAsync(bucket).ConfigureAwait(false);
            exists = true;
        }
        catch
        {
            exists = false;
        }

        exchange.In.Headers[S3Headers.BucketExists] = exists;
        exchange.In.Body = exists;
    }

    private async Task ProcessListBucketsAsync(IExchange exchange, CancellationToken ct)
    {
        var response = await _client!.ListBucketsAsync(ct).ConfigureAwait(false);
        exchange.In.Body = response.Buckets.Select(b => new { b.BucketName, b.CreationDate }).ToList();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  TAGGING
    // ═══════════════════════════════════════════════════════════════════

    private async Task ProcessGetObjectTaggingAsync(IExchange exchange, CancellationToken ct)
    {
        var bucket = ResolveBucket(exchange);
        var key = ResolveKey(exchange);

        var response = await _client!.GetObjectTaggingAsync(new GetObjectTaggingRequest
        {
            BucketName = bucket,
            Key = key,
        }, ct).ConfigureAwait(false);

        exchange.In.Body = response.Tagging.ToDictionary(t => t.Key, t => t.Value);
    }

    private async Task ProcessPutObjectTaggingAsync(IExchange exchange, CancellationToken ct)
    {
        var bucket = ResolveBucket(exchange);
        var key = ResolveKey(exchange);

        var tags = exchange.In.GetHeader<Dictionary<string, string>>(S3Headers.ObjectTags)
                   ?? (exchange.In.Body as Dictionary<string, string>)
                   ?? throw new InvalidOperationException($"Tags must be provided via header '{S3Headers.ObjectTags}' or body (Dictionary<string, string>).");

        await _client!.PutObjectTaggingAsync(new PutObjectTaggingRequest
        {
            BucketName = bucket,
            Key = key,
            Tagging = new Tagging { TagSet = tags.Select(kv => new Tag { Key = kv.Key, Value = kv.Value }).ToList() },
        }, ct).ConfigureAwait(false);
    }

    private async Task ProcessDeleteObjectTaggingAsync(IExchange exchange, CancellationToken ct)
    {
        var bucket = ResolveBucket(exchange);
        var key = ResolveKey(exchange);

        await _client!.DeleteObjectTaggingAsync(new DeleteObjectTaggingRequest
        {
            BucketName = bucket,
            Key = key,
        }, ct).ConfigureAwait(false);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ACL
    // ═══════════════════════════════════════════════════════════════════

    private async Task ProcessGetObjectAclAsync(IExchange exchange, CancellationToken ct)
    {
        var bucket = ResolveBucket(exchange);
        var key = ResolveKey(exchange);

        var response = await _client!.GetObjectAclAsync(new GetObjectAclRequest
        {
            BucketName = bucket,
            Key = key,
        }, ct).ConfigureAwait(false);

        exchange.In.Body = response.Grants;
    }

    private async Task ProcessPutObjectAclAsync(IExchange exchange, CancellationToken ct)
    {
        var bucket = ResolveBucket(exchange);
        var key = ResolveKey(exchange);

        var cannedAclStr = exchange.In.GetHeader<string>(S3Headers.CannedAcl) ?? _options.CannedAcl.ToString();

        await _client!.PutObjectAclAsync(new PutObjectAclRequest
        {
            BucketName = bucket,
            Key = key,
            ACL = MapCannedAcl(cannedAclStr),
        }, ct).ConfigureAwait(false);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  VERSIONING
    // ═══════════════════════════════════════════════════════════════════

    private async Task ProcessGetBucketVersioningAsync(IExchange exchange, CancellationToken ct)
    {
        var bucket = ResolveBucket(exchange);

        var response = await _client!.GetBucketVersioningAsync(new GetBucketVersioningRequest
        {
            BucketName = bucket,
        }, ct).ConfigureAwait(false);

        exchange.In.Body = response.VersioningConfig.Status?.Value ?? "Off";
        exchange.In.Headers[S3Headers.VersioningStatus] = response.VersioningConfig.Status?.Value ?? "Off";
    }

    private async Task ProcessPutBucketVersioningAsync(IExchange exchange, CancellationToken ct)
    {
        var bucket = ResolveBucket(exchange);
        var status = exchange.In.GetHeader<string>(S3Headers.VersioningStatus) ?? "Enabled";

        await _client!.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = bucket,
            VersioningConfig = new S3BucketVersioningConfig
            {
                Status = status.Equals("Enabled", StringComparison.OrdinalIgnoreCase)
                    ? VersionStatus.Enabled
                    : VersionStatus.Suspended,
            },
        }, ct).ConfigureAwait(false);

        Logger?.LogDebug("S3 PutBucketVersioning: {Bucket} → {Status}", bucket, status);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  RESTORE
    // ═══════════════════════════════════════════════════════════════════

    private async Task ProcessRestoreObjectAsync(IExchange exchange, CancellationToken ct)
    {
        var bucket = ResolveBucket(exchange);
        var key = ResolveKey(exchange);

        var days = exchange.In.GetHeader<int?>(S3Headers.RestoreDays) ?? 1;
        var tier = exchange.In.GetHeader<string>(S3Headers.RestoreTier) ?? "Standard";

        await _client!.RestoreObjectAsync(new RestoreObjectRequest
        {
            BucketName = bucket,
            Key = key,
            Days = days,
            Tier = tier switch
            {
                "Expedited" => GlacierJobTier.Expedited,
                "Bulk" => GlacierJobTier.Bulk,
                _ => GlacierJobTier.Standard,
            },
        }, ct).ConfigureAwait(false);

        Logger?.LogDebug("S3 RestoreObject: {Bucket}/{Key}, days={Days}, tier={Tier}", bucket, key, days, tier);
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
