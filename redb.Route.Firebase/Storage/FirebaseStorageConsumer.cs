using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
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

            // Idempotency check: seen in current or previous buffer
            if (_idempotent)
            {
                if (_currentIdempotentRepo!.ContainsKey(obj.Name)
                    || _previousIdempotentRepo?.ContainsKey(obj.Name) == true)
                    continue;
                _currentIdempotentRepo.TryAdd(obj.Name, true);
            }

            try
            {
                var exchange = await CreateExchange(obj, ct).ConfigureAwait(false);
                await ProcessWithTracking(exchange, ct).ConfigureAwait(false);

                // Post-process
                await PostProcessObject(obj.Name, ct).ConfigureAwait(false);

                processed++;
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Firebase Storage: error processing object {Object}", obj.Name);
                _endpoint.RecordError(ex);
            }
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

            body = ms.ToArray();
            _endpoint.RecordBytesIn(ms.Length);
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

        _endpoint.RecordMessageIn();
        return exchange;
    }

    private async Task PostProcessObject(string objectName, CancellationToken ct)
    {
        if (_options.MoveAfterRead is not null)
        {
            var destName = _options.MoveAfterRead + objectName;
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
                    "Firebase Storage: MoveAfterRead copied {Src} → {Dest} but delete of source failed. " +
                    "Object exists in both locations until next successful delete",
                    objectName, destName);
            }
        }
        else if (_options.DeleteAfterRead)
        {
            await _client!.DeleteObjectAsync(_endpoint.BucketName, objectName,
                cancellationToken: ct).ConfigureAwait(false);
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
