using System.Text.Json;
using Google.Cloud.Firestore;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Firebase;

/// <summary>
/// Firestore realtime consumer — subscribes to collection snapshots via gRPC stream.
/// Each document change (Added, Modified, Removed) produces a separate exchange.
/// Uses <see cref="InflightDrainGuard"/> for graceful shutdown (same pattern as MQTT consumer).
/// A permanently failed listener (revoked credentials, deleted project, callback fault) is
/// observed by a monitor task: the error is recorded on the endpoint and the subscription is
/// re-created with exponential backoff — the consumer never dies silently.
/// </summary>
internal sealed class FirestoreConsumer : IConsumer
{
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BackoffResetAfter = TimeSpan.FromMinutes(1);

    private readonly FirestoreEndpoint _endpoint;
    private readonly IProcessor _processor;
    private readonly FirestoreEndpointOptions _options;
    private readonly InflightDrainGuard _drain = new();
    private readonly SemaphoreSlim _concurrency;
    private FirestoreChangeListener? _listener;
    private Task? _monitorTask;
    private CancellationTokenSource? _monitorCts;
    private ILogger? _logger;

    /// <summary>Test seam: invoked with every snapshot BEFORE per-change processing.
    /// A throw here faults the listener — used to exercise the resubscribe monitor.</summary>
    internal Action<QuerySnapshot>? SnapshotInterceptor { get; set; }

    /// <inheritdoc />
    public IEndpoint Endpoint => _endpoint;

    internal FirestoreConsumer(FirestoreEndpoint endpoint, IProcessor processor, FirestoreEndpointOptions options)
    {
        _endpoint = endpoint;
        _processor = processor;
        _options = options;
        _concurrency = new SemaphoreSlim(options.MaxConcurrency, options.MaxConcurrency);
        _logger = (endpoint.Component as ComponentBase)?.Logger;
    }

    /// <inheritdoc />
    public async Task Start(CancellationToken ct = default)
    {
        _drain.Start(ct);

        var db = await _endpoint.GetOrCreateDbAsync(ct).ConfigureAwait(false);
        var query = BuildQuery(db);

        _monitorCts = new CancellationTokenSource();
        _listener = query.Listen(OnSnapshotAsync, _drain.ProcessingToken);
        _monitorTask = MonitorListenerAsync(query, _monitorCts.Token);

        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("Firestore consumer started: {Collection}", _endpoint.CollectionPath);
    }

    /// <inheritdoc />
    public async Task Stop(CancellationToken ct = default)
    {
        // 1. Stop the resubscribe monitor from creating new listeners
        _monitorCts?.Cancel();

        // 2. Stop accepting new snapshots
        var listener = _listener;
        if (listener is not null)
            await listener.StopAsync(ct).ConfigureAwait(false);

        // 3. Let the monitor settle (it may have swapped the listener before seeing the cancel)
        if (_monitorTask is not null)
        {
            try { await _monitorTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        if (!ReferenceEquals(_listener, listener) && _listener is not null)
            await _listener.StopAsync(ct).ConfigureAwait(false);

        // 4. Drain in-flight processing
        await _drain.DrainAsync(ct, _logger, $"fstore://{_endpoint.CollectionPath}").ConfigureAwait(false);

        _drain.Dispose();
        _monitorCts?.Dispose();
        _monitorCts = null;

        _logger?.LogInformation("Firestore consumer stopped: {Collection}", _endpoint.CollectionPath);
    }

    private Google.Cloud.Firestore.Query BuildQuery(FirestoreDb db)
    {
        Google.Cloud.Firestore.Query query = db.Collection(_endpoint.CollectionPath);

        if (_options.Where is not null)
            query = FirestoreQueryHelper.ApplyWhereFilters(query, _options.Where);
        if (_options.OrderBy is not null)
            query = FirestoreQueryHelper.ApplyOrderBy(query, _options.OrderBy);
        if (_options.Limit is not null)
            query = query.Limit(_options.Limit.Value);

        return query;
    }

    /// <summary>
    /// Watches the current listener's <see cref="FirestoreChangeListener.ListenerTask"/>.
    /// A graceful <c>StopAsync</c> ends the loop; a fault records the error and re-creates
    /// the subscription with exponential backoff (reset after a stable minute).
    /// </summary>
    private async Task MonitorListenerAsync(Google.Cloud.Firestore.Query query, CancellationToken ct)
    {
        var backoff = InitialBackoff;

        while (!ct.IsCancellationRequested)
        {
            var listener = _listener;
            if (listener is null) return;

            var startedAt = DateTime.UtcNow;
            try
            {
                await listener.ListenerTask.ConfigureAwait(false);
                return; // graceful stop
            }
            catch (OperationCanceledException)
            {
                return; // processing token cancelled — shutdown
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) return;

                _endpoint.RecordError(ex);
                if (DateTime.UtcNow - startedAt > BackoffResetAfter)
                    backoff = InitialBackoff;

                _logger?.LogError(ex,
                    "Firestore listener failed: {Collection}; resubscribing in {Delay}",
                    _endpoint.CollectionPath, backoff);
            }

            try
            {
                await Task.Delay(backoff, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, MaxBackoff.TotalSeconds));

            if (ct.IsCancellationRequested) return;
            _listener = query.Listen(OnSnapshotAsync, _drain.ProcessingToken);
            _logger?.LogInformation("Firestore listener resubscribed: {Collection}", _endpoint.CollectionPath);
        }
    }

    private async Task OnSnapshotAsync(QuerySnapshot snapshot, CancellationToken ct)
    {
        SnapshotInterceptor?.Invoke(snapshot);

        var tasks = new List<Task>();
        foreach (var change in snapshot.Changes)
        {
            if (ct.IsCancellationRequested)
                break;

            _drain.Increment();
            tasks.Add(ProcessChangeAsync(change, ct));
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task ProcessChangeAsync(DocumentChange change, CancellationToken ct)
    {
        // MaxConcurrency: an initial snapshot delivers the WHOLE collection at once —
        // unbounded parallel processing would flood the pipeline.
        try
        {
            await _concurrency.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _drain.Decrement(); // Increment happened in OnSnapshotAsync; never throw into the listener
            return;
        }

        Exchange? exchange = null;
        try
        {
            var data = change.Document.ToDictionary();
            object body = _options.RawJson
                ? JsonSerializer.Serialize(data)
                : data;

            exchange = Exchange.Create(new Message(body), _endpoint.ScopeFactory);

            exchange.In.Headers[FirestoreHeaders.DocumentId] = change.Document.Id;
            exchange.In.Headers[FirestoreHeaders.DocumentPath] = change.Document.Reference.Path;
            exchange.In.Headers[FirestoreHeaders.CollectionPath] = _endpoint.CollectionPath;
            exchange.In.Headers[FirestoreHeaders.ChangeType] = change.ChangeType.ToString();
            exchange.In.Headers[FirestoreHeaders.CreateTime] = change.Document.CreateTime;
            exchange.In.Headers[FirestoreHeaders.UpdateTime] = change.Document.UpdateTime;
            exchange.In.Headers[FirestoreHeaders.ReadTime] = change.Document.ReadTime;

            // MessagesIn is counted by the core StatisticsProcessor - ownership audit.

            await _processor.Process(exchange, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Pipeline errors are counted by the core StatisticsProcessor (ownership audit).
            _logger?.LogError(ex, "Firestore change processing failed: {Collection}/{DocId}",
                _endpoint.CollectionPath, change.Document.Id);
        }
        finally
        {
            if (exchange is not null)
                await exchange.DisposeAsync().ConfigureAwait(false);
            _drain.Decrement();
            _concurrency.Release();
        }
    }

}
