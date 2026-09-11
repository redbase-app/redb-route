using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;

namespace redb.Route.Firebase;

/// <summary>
/// Firebase Storage consumer — polls a bucket for objects using ListObjectsAsync.
/// Extends <see cref="DrainableConsumer"/> for graceful two-CTS shutdown.
/// Supports prefix/include/exclude filtering, idempotency, delete-after-read,
/// and move-after-read (copy + delete).
/// </summary>
internal sealed class FirebaseStorageConsumer : DrainableConsumer
{
    private readonly FirebaseStorageEndpoint _endpoint;
    private readonly FirebaseStorageEndpointOptions _options;
    private StorageClient? _client;

    private const int MaxIdempotentEntries = 10_000;
    private ConcurrentDictionary<string, bool>? _currentIdempotentRepo;
    private ConcurrentDictionary<string, bool>? _previousIdempotentRepo;
    private readonly bool _idempotent;

    // Named repository (Д4): the shared IIdempotentRepository contract of the route-level
    // IdempotentConsumer EIP — dedup survives consumer restarts (and process restarts with
    // a persistent implementation).
    private IIdempotentRepository? _sharedRepo;
    private readonly Regex? _includeRegex;
    private readonly Regex? _excludeRegex;

    /// <inheritdoc />
    protected override IEndpoint ConsumerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ConsumerName => $"fbstorage://{_endpoint.BucketName}";

    internal FirebaseStorageConsumer(FirebaseStorageEndpoint endpoint, IProcessor processor,
        FirebaseStorageEndpointOptions options) : base(processor)
    {
        _endpoint = endpoint;
        _options = options;

        if (options.Idempotent)
        {
            _idempotent = true;
            _currentIdempotentRepo = new ConcurrentDictionary<string, bool>();
        }

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
                await _client.CreateBucketAsync(_endpoint.RequireProjectId(), _endpoint.BucketName,
                    cancellationToken: ct).ConfigureAwait(false);
            }
            catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.Conflict)
            {
                // already exists
            }
        }
    }

    /// <inheritdoc />
    protected override async Task RunAsync(CancellationToken pollCt, CancellationToken processingCt)
    {
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
                Logger?.LogError(ex, "Firebase Storage consumer poll error on bucket {Bucket}",
                    _endpoint.BucketName);
                _endpoint.RecordError(ex);
            }

            try
            {
                await Task.Delay(_options.Delay, pollCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (pollCt.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task PollBucketAsync(CancellationToken ct)
    {
        var prefix = _options.Prefix ?? _endpoint.ObjectPrefix;
        var objects = _client!.ListObjectsAsync(_endpoint.BucketName, prefix);
        var processed = 0;

        // Double-buffer eviction: when current fills up, rotate to previous
        // and start fresh — retains up to 2×MaxIdempotentEntries of history.
        if (_idempotent && _currentIdempotentRepo!.Count > MaxIdempotentEntries)
        {
            _previousIdempotentRepo = _currentIdempotentRepo;
            _currentIdempotentRepo = new ConcurrentDictionary<string, bool>();
        }

        await foreach (var obj in objects.ConfigureAwait(false))
        {
            if (ct.IsCancellationRequested || processed >= _options.MaxMessagesPerPoll)
                break;

            // Filter by include/exclude
            if (_includeRegex is not null && !_includeRegex.IsMatch(obj.Name))
                continue;
            if (_excludeRegex is not null && _excludeRegex.IsMatch(obj.Name))
                continue;

            // Idempotency: check only — the "seen" mark is written after a SUCCESSFUL
            // exchange, so a failed object is offered again on the next poll.
            if (_idempotent
                && (_currentIdempotentRepo!.ContainsKey(obj.Name)
                    || _previousIdempotentRepo?.ContainsKey(obj.Name) == true))
                continue;
            if (_sharedRepo is not null
                && await _sharedRepo.Contains(SharedKey(obj.Name), ct).ConfigureAwait(false))
                continue;

            // Named repo: two-phase claim (Add → process → Confirm / Remove), same as the
            // route-level EIP — a concurrent consumer cannot take the same object twice.
            if (_sharedRepo is not null
                && !await _sharedRepo.Add(SharedKey(obj.Name), ct).ConfigureAwait(false))
                continue;

            bool success;
            try
            {
                var exchange = await CreateExchange(obj, ct).ConfigureAwait(false);
                success = await ProcessExchangeAsync(exchange, obj.Name, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Download/exchange creation failed — the object was never delivered:
                // no post-processing, no idempotent mark, retry on the next poll.
                Logger?.LogError(ex, "Firebase Storage: error reading object {Object}", obj.Name);
                _endpoint.RecordError(ex);
                await ReleaseSharedClaimAsync(obj.Name, ct).ConfigureAwait(false);
                processed++;
                continue;
            }

            try
            {
                if (success)
                {
                    if (_idempotent)
                        _currentIdempotentRepo!.TryAdd(obj.Name, true);
                    if (_sharedRepo is not null)
                        await _sharedRepo.Confirm(SharedKey(obj.Name), ct).ConfigureAwait(false);
                    await PostProcessObject(obj.Name, ct).ConfigureAwait(false);
                }
                else
                {
                    await ReleaseSharedClaimAsync(obj.Name, ct).ConfigureAwait(false);
                    if (_options.MoveFailed is not null)
                        await MoveObjectAsync(obj.Name, _options.MoveFailed, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Firebase Storage: post-processing failed for {Object}", obj.Name);
                _endpoint.RecordError(ex);
            }

            processed++;
        }
    }

    /// <summary>
    /// Runs the exchange through the processor with inflight tracking and reports whether it
    /// succeeded. A processing failure must not delete/move the object (data loss), so unlike
    /// <see cref="DrainableConsumer.ProcessWithTracking"/> the outcome is observed here:
    /// both a raw throw (no error handler took the exchange) and an unhandled
    /// <c>exchange.Exception</c> set by the pipeline count as failure.
    /// </summary>
    private async Task<bool> ProcessExchangeAsync(Exchange exchange, string objectName, CancellationToken ct)
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
            Logger?.LogError(ex, "Firebase Storage: processing failed for {Object}; object is kept.", objectName);
            return false;
        }
        finally
        {
            DecrementInflight();
            await exchange.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<Exchange> CreateExchange(Google.Apis.Storage.v1.Data.Object obj, CancellationToken ct)
    {
        object? body = null;

        if (_options.IncludeBody)
        {
            var ms = new MemoryStream();
            await _client!.DownloadObjectAsync(_endpoint.BucketName, obj.Name, ms,
                cancellationToken: ct).ConfigureAwait(false);

            body = ms.ToArray(); // BytesIn is the core's estimate of the same payload
        }

        var exchange = Exchange.Create(new Message(body), _endpoint.ScopeFactory);
        exchange.In.Headers[FirebaseStorageHeaders.ObjectName] = obj.Name;
        exchange.In.Headers[FirebaseStorageHeaders.BucketName] = _endpoint.BucketName;
        exchange.In.Headers[FirebaseStorageHeaders.ContentType] = obj.ContentType;
        exchange.In.Headers[FirebaseStorageHeaders.ContentLength] = obj.Size;
        exchange.In.Headers[FirebaseStorageHeaders.Md5Hash] = obj.Md5Hash;
        exchange.In.Headers[FirebaseStorageHeaders.Generation] = obj.Generation;
        exchange.In.Headers[FirebaseStorageHeaders.TimeCreated] = GcsDateTimeHelper.SafeParse(() => obj.TimeCreatedDateTimeOffset, obj.TimeCreatedRaw);
        exchange.In.Headers[FirebaseStorageHeaders.Updated] = GcsDateTimeHelper.SafeParse(() => obj.UpdatedDateTimeOffset, obj.UpdatedRaw);
        exchange.In.Headers[FirebaseStorageHeaders.MediaLink] = obj.MediaLink;

        // MessagesIn is counted by the core StatisticsProcessor - ownership audit.
        return exchange;
    }

    private async Task PostProcessObject(string objectName, CancellationToken ct)
    {
        if (_options.MoveAfterRead is not null)
        {
            await MoveObjectAsync(objectName, _options.MoveAfterRead, ct).ConfigureAwait(false);
        }
        else if (_options.DeleteAfterRead)
        {
            await _client!.DeleteObjectAsync(_endpoint.BucketName, objectName,
                cancellationToken: ct).ConfigureAwait(false);
        }
    }

    private string SharedKey(string objectName) => $"{_endpoint.BucketName}/{objectName}";

    /// <summary>Releases the two-phase claim so a failed object is offered again (Д4).</summary>
    private async Task ReleaseSharedClaimAsync(string objectName, CancellationToken ct)
    {
        if (_sharedRepo is null) return;
        try
        {
            await _sharedRepo.Remove(SharedKey(objectName), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger?.LogWarning(ex,
                "Firebase Storage: releasing idempotent claim for {Object} failed — the object stays marked until manual cleanup",
                objectName);
        }
    }

    private async Task MoveObjectAsync(string objectName, string destPrefix, CancellationToken ct)
    {
        var destName = destPrefix + objectName;
        await _client!.CopyObjectAsync(_endpoint.BucketName, objectName,
            _endpoint.BucketName, destName, cancellationToken: ct).ConfigureAwait(false);
        try
        {
            await _client.DeleteObjectAsync(_endpoint.BucketName, objectName,
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Copy succeeded but delete failed — object is duplicated.
            // Log and continue; next poll will skip via idempotency or retry delete.
            Logger?.LogWarning(ex,
                "Firebase Storage: move copied {Src} → {Dest} but delete of source failed. " +
                "Object exists in both locations until next successful delete",
                objectName, destName);
        }
    }

    private static Regex GlobToRegex(string glob)
    {
        var pattern = "^" + Regex.Escape(glob)
            .Replace("\\*\\*", ".*")
            .Replace("\\*", "[^/]*")
            .Replace("\\?", "[^/]") + "$";
        return new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
    }
}
