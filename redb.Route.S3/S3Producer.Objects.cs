using System.Diagnostics;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.S3;

/// <summary>Object operations and presigned URLs of <see cref="S3Producer"/> (S-12 распил).</summary>
internal sealed partial class S3Producer
{
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

        // Conditional PUT (S-5): create-only semantics — fails with 412 when the key exists.
        if (_options.ConditionalWrite)
            request.IfNoneMatch = "*";

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

}
