using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.S3;

/// <summary>
/// S3 consumer — polls a bucket for objects using ListObjectsV2.
/// Extends <see cref="DrainableConsumer"/> for graceful two-CTS shutdown.
/// <para>
/// Supports prefix/include/exclude filtering, sort order, min/max age,
/// idempotency, max-messages-per-poll, delete-after-read, move-after-read
/// with destination bucket/prefix/suffix, and done-file markers.
/// Inspired by Apache Camel aws2-s3 consumer patterns.
/// </para>
/// </summary>
internal sealed class S3Consumer : DrainableConsumer
{
    private readonly S3Endpoint _endpoint;
    private readonly S3EndpointOptions _options;
    private IAmazonS3? _client;
    private long _processedCount;

    // In-memory idempotent repository: key → true
    private readonly ConcurrentDictionary<string, bool>? _idempotentRepo;

    // Include/Exclude patterns compiled from glob
    private readonly Regex? _includeRegex;
    private readonly Regex? _excludeRegex;

    /// <inheritdoc />
    protected override IEndpoint ConsumerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ConsumerName => $"s3://{_endpoint.BucketName}";

    /// <summary>Number of objects processed since start.</summary>
    internal long ProcessedCount => Interlocked.Read(ref _processedCount);

    internal S3Consumer(S3Endpoint endpoint, IProcessor processor, S3EndpointOptions options)
        : base(processor)
    {
        _endpoint = endpoint;
        _options = options;

        if (options.Idempotent)
            _idempotentRepo = new ConcurrentDictionary<string, bool>();

        if (!string.IsNullOrEmpty(options.Include))
            _includeRegex = GlobToRegex(options.Include);

        if (!string.IsNullOrEmpty(options.Exclude))
            _excludeRegex = GlobToRegex(options.Exclude);
    }

    /// <inheritdoc />
    protected override async Task OnStarting(CancellationToken ct)
    {
        _client = await _endpoint.GetOrCreateClientAsync(ct).ConfigureAwait(false);

        if (_options.AutoCreateBucket)
        {
            try
            {
                await _client.EnsureBucketExistsAsync(_endpoint.BucketName).ConfigureAwait(false);
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                // Already exists
            }
        }
    }

    /// <inheritdoc />
    protected override async Task RunAsync(CancellationToken pollCt, CancellationToken processingCt)
    {
        // Initial delay
        if (_options.InitialDelay > 0)
            await Task.Delay(_options.InitialDelay, pollCt).ConfigureAwait(false);

        while (!pollCt.IsCancellationRequested)
        {
            try
            {
                await PollBucketAsync(processingCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (pollCt.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "S3 consumer poll error on bucket {Bucket}", _endpoint.BucketName);
                _endpoint.RecordError();
            }

            // Wait between polls
            try
            {
                await Task.Delay(_options.Delay, pollCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  POLLING
    // ═══════════════════════════════════════════════════════════════════

    private async Task PollBucketAsync(CancellationToken ct)
    {
        // If fileName is specified, handle single-file mode
        if (!string.IsNullOrEmpty(_options.FileName))
        {
            await PollSingleFileAsync(ct).ConfigureAwait(false);
            return;
        }

        var request = new ListObjectsV2Request
        {
            BucketName = _endpoint.BucketName,
            Prefix = _options.Prefix,
            Delimiter = _options.Delimiter,
        };

        var allObjects = new List<S3Object>();
        string? continuationToken = null;

        // Paginate through listing
        do
        {
            request.ContinuationToken = continuationToken;
            var response = await _client!.ListObjectsV2Async(request, ct).ConfigureAwait(false);

            foreach (var obj in response.S3Objects ?? [])
            {
                // Skip folder markers
                if (!_options.IncludeFolders && obj.Key.EndsWith('/'))
                    continue;

                allObjects.Add(obj);
            }

            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        } while (continuationToken != null);

        // Apply filters
        var eligible = FilterObjects(allObjects);

        // Sort
        eligible = SortObjects(eligible);

        // Apply max-messages-per-poll
        if (_options.MaxMessagesPerPoll > 0)
            eligible = eligible.Take(_options.MaxMessagesPerPoll).ToList();

        if (eligible.Count == 0)
        {
            if (_options.SendEmptyMessageWhenIdle)
            {
                var emptyExchange = CreateExchange(null);
                await ProcessWithTracking(emptyExchange, ct).ConfigureAwait(false);
            }
            return;
        }

        // Process each object
        foreach (var obj in eligible)
        {
            if (ct.IsCancellationRequested)
                break;

            await ProcessObjectAsync(obj, ct).ConfigureAwait(false);
        }
    }

    private async Task PollSingleFileAsync(CancellationToken ct)
    {
        try
        {
            var metadata = await _client!.GetObjectMetadataAsync(_endpoint.BucketName, _options.FileName, ct)
                .ConfigureAwait(false);

            var pseudoObj = new S3Object
            {
                BucketName = _endpoint.BucketName,
                Key = _options.FileName,
                Size = metadata.ContentLength,
                LastModified = metadata.LastModified,
                ETag = metadata.ETag,
            };

            // Check idempotency
            if (_idempotentRepo != null)
            {
                var idempotentKey = BuildIdempotentKey(pseudoObj);
                if (!_idempotentRepo.TryAdd(idempotentKey, true))
                    return;
            }

            await ProcessObjectAsync(pseudoObj, ct).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // File doesn't exist yet — skip
        }
    }

    private async Task ProcessObjectAsync(S3Object obj, CancellationToken ct)
    {
        // Done file check
        if (!string.IsNullOrEmpty(_options.DoneFileName))
        {
            var doneKey = ResolveDoneFileName(obj.Key);
            try
            {
                await _client!.GetObjectMetadataAsync(_endpoint.BucketName, doneKey, ct).ConfigureAwait(false);
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Logger?.LogDebug("S3: Skipping {Key} — done file {DoneKey} not found", obj.Key, doneKey);
                return;
            }
        }

        // Download object
        object? body = null;
        GetObjectResponse? getResponse = null;

        if (!_options.IgnoreBody)
        {
            var getRequest = new GetObjectRequest
            {
                BucketName = _endpoint.BucketName,
                Key = obj.Key,
            };
            getResponse = await _client!.GetObjectAsync(getRequest, ct).ConfigureAwait(false);

            if (_options.StreamBody)
            {
                body = getResponse.ResponseStream;
            }
            else if (_options.IncludeBody)
            {
                using var ms = new MemoryStream();
                await getResponse.ResponseStream.CopyToAsync(ms, ct).ConfigureAwait(false);
                body = ms.ToArray();
                getResponse.ResponseStream.Dispose();
                _endpoint.RecordBytesIn(ms.Length);
            }
            else
            {
                body = getResponse.ResponseStream;
            }
        }

        // Create exchange
        var exchange = CreateExchange(body);
        SetConsumerHeaders(exchange, obj, getResponse);

        // Process
        _endpoint.RecordMessageIn();
        await ProcessWithTracking(exchange, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _processedCount);

        // Post-processing (only on success)
        if (exchange.Exception == null || exchange.ExceptionHandled)
        {
            await PostProcessAsync(obj, ct).ConfigureAwait(false);
        }
    }

    private async Task PostProcessAsync(S3Object obj, CancellationToken ct)
    {
        // Move-after-read
        if (_options.MoveAfterRead && !string.IsNullOrEmpty(_options.DestinationBucket))
        {
            var destKey = BuildDestinationKey(obj.Key);

            if (_options.AutoCreateBucket)
            {
                try
                {
                    await _client!.EnsureBucketExistsAsync(_options.DestinationBucket).ConfigureAwait(false);
                }
                catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict) { }
            }

            await _client!.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = _endpoint.BucketName,
                SourceKey = obj.Key,
                DestinationBucket = _options.DestinationBucket,
                DestinationKey = destKey,
            }, ct).ConfigureAwait(false);

            // Delete from source after successful copy
            await _client.DeleteObjectAsync(_endpoint.BucketName, obj.Key, ct).ConfigureAwait(false);

            Logger?.LogDebug("S3: Moved {Bucket}/{Key} → {DestBucket}/{DestKey}",
                _endpoint.BucketName, obj.Key, _options.DestinationBucket, destKey);
        }
        else if (_options.DeleteAfterRead)
        {
            await _client!.DeleteObjectAsync(_endpoint.BucketName, obj.Key, ct).ConfigureAwait(false);
            Logger?.LogDebug("S3: Deleted {Bucket}/{Key} after read", _endpoint.BucketName, obj.Key);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  FILTERING
    // ═══════════════════════════════════════════════════════════════════

    private List<S3Object> FilterObjects(List<S3Object> objects)
    {
        var now = DateTime.UtcNow;

        return objects.Where(obj =>
        {
            // Extract filename (last segment) for glob matching
            var fileName = obj.Key.Contains('/')
                ? obj.Key[(obj.Key.LastIndexOf('/') + 1)..]
                : obj.Key;

            // Include pattern
            if (_includeRegex != null && !_includeRegex.IsMatch(fileName))
                return false;

            // Exclude pattern
            if (_excludeRegex != null && _excludeRegex.IsMatch(fileName))
                return false;

            // Min age
            if (_options.MinAge > 0)
            {
                var age = (now - (obj.LastModified ?? now)).TotalMilliseconds;
                if (age < _options.MinAge)
                    return false;
            }

            // Max age
            if (_options.MaxAge > 0)
            {
                var age = (now - (obj.LastModified ?? now)).TotalMilliseconds;
                if (age > _options.MaxAge)
                    return false;
            }

            // Idempotency
            if (_idempotentRepo != null)
            {
                var idempotentKey = BuildIdempotentKey(obj);
                if (!_idempotentRepo.TryAdd(idempotentKey, true))
                    return false;
            }

            return true;
        }).ToList();
    }

    private List<S3Object> SortObjects(List<S3Object> objects)
    {
        return _options.SortBy switch
        {
            S3SortBy.Key => objects.OrderBy(o => o.Key).ToList(),
            S3SortBy.KeyDesc => objects.OrderByDescending(o => o.Key).ToList(),
            S3SortBy.LastModified => objects.OrderBy(o => o.LastModified).ToList(),
            S3SortBy.LastModifiedDesc => objects.OrderByDescending(o => o.LastModified).ToList(),
            S3SortBy.Size => objects.OrderBy(o => o.Size).ToList(),
            S3SortBy.SizeDesc => objects.OrderByDescending(o => o.Size).ToList(),
            _ => objects,
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  EXCHANGE CREATION
    // ═══════════════════════════════════════════════════════════════════

    private IExchange CreateExchange(object? body)
    {
        var message = new Message { Body = body };
        return new Exchange(message);
    }

    private void SetConsumerHeaders(IExchange exchange, S3Object obj, GetObjectResponse? response)
    {
        var headers = exchange.In.Headers;
        headers[S3Headers.BucketName] = obj.BucketName ?? _endpoint.BucketName;
        headers[S3Headers.Key] = obj.Key;
        headers[S3Headers.ContentLength] = obj.Size;
        headers[S3Headers.ETag] = obj.ETag;
        headers[S3Headers.LastModified] = obj.LastModified;
        headers[S3Headers.StorageClass] = obj.StorageClass?.Value;
        headers[S3Headers.Timestamp] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (response != null)
        {
            headers[S3Headers.ContentType] = response.Headers.ContentType;
            headers[S3Headers.VersionId] = response.VersionId;
            headers[S3Headers.ServerSideEncryption] = response.ServerSideEncryptionMethod?.Value;

            // User metadata
            foreach (var meta in response.Metadata.Keys)
                headers[$"{S3Headers.MetadataPrefix}{meta}"] = response.Metadata[meta];
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════════════════════

    private string BuildIdempotentKey(S3Object obj)
    {
        if (!string.IsNullOrEmpty(_options.IdempotentKey))
            return _options.IdempotentKey; // TODO: expression evaluation

        return $"{obj.Key}|{obj.ETag}|{obj.Size}";
    }

    private string BuildDestinationKey(string sourceKey)
    {
        var key = sourceKey;

        // Remove source prefix
        if (_options.RemovePrefixOnMove && !string.IsNullOrEmpty(_options.Prefix) && key.StartsWith(_options.Prefix))
            key = key[_options.Prefix.Length..];

        // Add destination prefix/suffix
        if (!string.IsNullOrEmpty(_options.DestinationBucketPrefix))
            key = _options.DestinationBucketPrefix + key;

        if (!string.IsNullOrEmpty(_options.DestinationBucketSuffix))
        {
            var lastDot = key.LastIndexOf('.');
            if (lastDot > 0)
                key = key[..lastDot] + _options.DestinationBucketSuffix + key[lastDot..];
            else
                key += _options.DestinationBucketSuffix;
        }

        return key;
    }

    private string ResolveDoneFileName(string objectKey)
    {
        // Simple pattern: "${file:name}.done" → "objectKey.done"
        return _options.DoneFileName.Replace("${file:name}", objectKey);
    }

    private static Regex GlobToRegex(string glob)
    {
        // Support comma-separated patterns
        var patterns = glob.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var regexPatterns = patterns.Select(p =>
        {
            var escaped = Regex.Escape(p)
                .Replace(@"\*\*", ".*")    // ** matches anything including /
                .Replace(@"\*", "[^/]*")   // * matches anything except /
                .Replace(@"\?", "[^/]");   // ? matches single char except /
            return $"(?:{escaped})";
        });

        return new Regex($"^(?:{string.Join("|", regexPatterns)})$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
    }
}
