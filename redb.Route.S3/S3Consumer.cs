using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Components;
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

    // In-memory idempotent repository with double-buffer eviction (S-6): when current fills
    // up it rotates to previous and starts fresh — retains up to 2×MaxIdempotentEntries of
    // history instead of growing without bound.
    private const int MaxIdempotentEntries = 10_000;
    private ConcurrentDictionary<string, bool>? _idempotentRepo;
    private ConcurrentDictionary<string, bool>? _previousIdempotentRepo;

    // Named repository (Д4): the shared IIdempotentRepository contract of the route-level
    // IdempotentConsumer EIP — dedup survives consumer restarts (and process restarts with
    // a persistent implementation).
    private IIdempotentRepository? _sharedRepo;

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

        if (!string.IsNullOrEmpty(_options.IdempotentRepository))
        {
            var context = (_endpoint.Component as ComponentBase)?.Context
                          ?? throw new InvalidOperationException(
                              $"IdempotentRepository '{_options.IdempotentRepository}' requires a route context " +
                              "(register the repository with context.AddIdempotentRepository(name, ...)).");
            _sharedRepo = context.GetIdempotentRepositoryProvider().Get(_options.IdempotentRepository);
        }

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
                _endpoint.RecordError(ex); // с деталями — иначе LastErrorMessage пуст (S-11)
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

        // Without sorting the listing streams page by page with an early exit at
        // MaxMessagesPerPoll — a big bucket no longer costs O(N) memory per poll (S-7).
        if (_options.SortBy == S3SortBy.None)
        {
            await PollStreamingAsync(ct).ConfigureAwait(false);
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

        // Paginate through listing — SortBy honestly needs the full list in memory.
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

    /// <summary>
    /// Streaming poll for the unsorted case (S-7): pages are processed as they arrive and the
    /// loop exits as soon as <c>MaxMessagesPerPoll</c> eligible objects were handled.
    /// </summary>
    private async Task PollStreamingAsync(CancellationToken ct)
    {
        var request = new ListObjectsV2Request
        {
            BucketName = _endpoint.BucketName,
            Prefix = _options.Prefix,
            Delimiter = _options.Delimiter,
        };

        var processed = 0;
        var anyEligible = false;
        var now = DateTime.UtcNow;
        string? continuationToken = null;

        do
        {
            request.ContinuationToken = continuationToken;
            var response = await _client!.ListObjectsV2Async(request, ct).ConfigureAwait(false);

            foreach (var obj in response.S3Objects ?? [])
            {
                if (ct.IsCancellationRequested)
                    return;

                if (!_options.IncludeFolders && obj.Key.EndsWith('/'))
                    continue;
                if (!PassesFilters(obj, now))
                    continue;

                anyEligible = true;
                await ProcessObjectAsync(obj, ct).ConfigureAwait(false);

                if (_options.MaxMessagesPerPoll > 0 && ++processed >= _options.MaxMessagesPerPoll)
                    return;
            }

            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        } while (continuationToken != null);

        if (!anyEligible && _options.SendEmptyMessageWhenIdle)
        {
            var emptyExchange = CreateExchange(null);
            await ProcessWithTracking(emptyExchange, ct).ConfigureAwait(false);
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

            // Idempotency: check only — the mark is written after success in ProcessObjectAsync (S-2)
            if (IsAlreadySeen(pseudoObj))
                return;

            await ProcessObjectAsync(pseudoObj, ct).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // File doesn't exist yet — skip
        }
    }

    private async Task ProcessObjectAsync(S3Object obj, CancellationToken ct)
    {
        // Named repo (Д4): two-phase claim BEFORE the download — Add is atomic, so a
        // concurrent consumer cannot take the same object twice; released on any failure.
        if (_sharedRepo is not null
            && !await _sharedRepo.Add(BuildIdempotentKey(obj), ct).ConfigureAwait(false))
            return;

        try
        {
            await ProcessObjectCoreAsync(obj, ct).ConfigureAwait(false);
        }
        catch
        {
            await ReleaseSharedClaimAsync(obj, ct).ConfigureAwait(false);
            throw;
        }
    }

    private async Task ProcessObjectCoreAsync(S3Object obj, CancellationToken ct)
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
                // Release the Д4 claim: the object must be re-offered once the done file appears.
                await ReleaseSharedClaimAsync(obj, ct).ConfigureAwait(false);
                return;
            }
        }

        // Download object. IncludeBody=false (and IgnoreBody) mean "no content download" —
        // no GET is issued at all; previously a raw ResponseStream leaked into the body (S-8).
        object? body = null;
        GetObjectResponse? getResponse = null;

        if (!_options.IgnoreBody && (_options.StreamBody || _options.IncludeBody))
        {
            var getRequest = new GetObjectRequest
            {
                BucketName = _endpoint.BucketName,
                Key = obj.Key,
            };
            getResponse = await _client!.GetObjectAsync(getRequest, ct).ConfigureAwait(false);

            if (_options.StreamBody)
            {
                // Ownership: the stream goes into the exchange; Exchange.DisposeAsync closes
                // it, which releases the HTTP connection. The response object must NOT be
                // disposed here — that would close the stream we just handed over (S-8).
                body = getResponse.ResponseStream;
            }
            else
            {
                try
                {
                    using var ms = new MemoryStream();
                    await getResponse.ResponseStream.CopyToAsync(ms, ct).ConfigureAwait(false);
                    body = ms.ToArray(); // BytesIn is counted by the core around the routed consumer
                }
                catch
                {
                    getResponse.Dispose();
                    throw;
                }
            }
        }

        // Create exchange
        var exchange = CreateExchange(body);
        SetConsumerHeaders(exchange, obj, getResponse);

        // Buffered path: the response is fully read and its headers copied — release the
        // connection now instead of leaking it until GC (S-8).
        if (getResponse is not null && !_options.StreamBody)
            getResponse.Dispose();

        // Process (MessagesIn is counted by the core StatisticsProcessor - ownership audit)
        var success = await ProcessExchangeAsync(exchange, obj.Key, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _processedCount);

        if (success)
        {
            // Idempotent mark only AFTER success (S-2): a failed object and objects beyond
            // MaxMessagesPerPoll must be offered again on the next poll.
            MarkSeen(obj);
            if (_sharedRepo is not null)
                await _sharedRepo.Confirm(BuildIdempotentKey(obj), ct).ConfigureAwait(false);
            await PostProcessAsync(obj, ct).ConfigureAwait(false);
        }
        else
        {
            await ReleaseSharedClaimAsync(obj, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Releases the two-phase claim so a failed object is offered again (Д4).</summary>
    private async Task ReleaseSharedClaimAsync(S3Object obj, CancellationToken ct)
    {
        if (_sharedRepo is null) return;
        try
        {
            await _sharedRepo.Remove(BuildIdempotentKey(obj), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger?.LogWarning(ex,
                "S3: releasing idempotent claim for {Key} failed — the object stays marked until manual cleanup",
                obj.Key);
        }
    }

    /// <summary>
    /// Runs the exchange through the processor with inflight tracking and reports whether it
    /// succeeded. A processing failure must not delete/move the object (S-1), so unlike
    /// <see cref="DrainableConsumer.ProcessWithTracking"/> the outcome is observed here:
    /// both a raw throw (no error handler took the exchange) and an unhandled
    /// <c>exchange.Exception</c> set by the pipeline count as failure.
    /// </summary>
    private async Task<bool> ProcessExchangeAsync(IExchange exchange, string key, CancellationToken ct)
    {
        IncrementInflight();
        try
        {
            await Processor.Process(exchange, ct).ConfigureAwait(false);
            return exchange.Exception is null || exchange.ExceptionHandled;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // shutdown — let the poll loop exit
        }
        catch (Exception ex)
        {
            // Pipeline errors are counted by the core StatisticsProcessor (ownership audit).
            Logger?.LogError(ex, "S3: processing failed for {Key}; object is kept.", key);
            return false;
        }
        finally
        {
            DecrementInflight();
            await exchange.DisposeAsync().ConfigureAwait(false);
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

            // Delete from source after successful copy. A failed delete must not bubble as a
            // poll error (S-9): the copy already succeeded — log the duplication and move on;
            // the idempotent mark (when enabled) keeps the source from being re-processed.
            try
            {
                await _client.DeleteObjectAsync(_endpoint.BucketName, obj.Key, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger?.LogWarning(ex,
                    "S3: MoveAfterRead copied {Bucket}/{Key} → {DestBucket}/{DestKey} but delete of source failed. " +
                    "Object exists in both locations until the next successful delete",
                    _endpoint.BucketName, obj.Key, _options.DestinationBucket, destKey);
            }

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
        return objects.Where(obj => PassesFilters(obj, now)).ToList();
    }

    private bool PassesFilters(S3Object obj, DateTime now)
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

        // Idempotency: check only — the "seen" mark is written after a SUCCESSFUL
        // exchange (S-2), never at filter time.
        if (IsAlreadySeen(obj))
            return false;

        return true;
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
        // ScopeFactory from the endpoint — otherwise per-exchange DI scopes are dead (S-4).
        return Exchange.Create(new Message { Body = body }, _endpoint.ScopeFactory);
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

    private static string BuildIdempotentKey(S3Object obj)
        => $"{obj.Key}|{obj.ETag}|{obj.Size}";

    private bool IsAlreadySeen(S3Object obj)
    {
        if (_idempotentRepo is null) return false;
        var key = BuildIdempotentKey(obj);
        return _idempotentRepo.ContainsKey(key)
               || _previousIdempotentRepo?.ContainsKey(key) == true;
    }

    private void MarkSeen(S3Object obj)
    {
        if (_idempotentRepo is null) return;

        // Double-buffer eviction (S-6)
        if (_idempotentRepo.Count > MaxIdempotentEntries)
        {
            _previousIdempotentRepo = _idempotentRepo;
            _idempotentRepo = new ConcurrentDictionary<string, bool>();
        }
        _idempotentRepo.TryAdd(BuildIdempotentKey(obj), true);
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
