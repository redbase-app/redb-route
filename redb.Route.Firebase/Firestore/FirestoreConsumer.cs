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
/// </summary>
internal sealed class FirestoreConsumer : IConsumer
{
    private readonly FirestoreEndpoint _endpoint;
    private readonly IProcessor _processor;
    private readonly FirestoreEndpointOptions _options;
    private readonly InflightDrainGuard _drain = new();
    private FirestoreChangeListener? _listener;
    private ILogger? _logger;

    /// <inheritdoc />
    public IEndpoint Endpoint => _endpoint;

    internal FirestoreConsumer(FirestoreEndpoint endpoint, IProcessor processor, FirestoreEndpointOptions options)
    {
        _endpoint = endpoint;
        _processor = processor;
        _options = options;
        _logger = (endpoint.Component as ComponentBase)?.Logger;
    }

    /// <inheritdoc />
    public async Task Start(CancellationToken ct = default)
    {
        _drain.Start(ct);

        var db = await _endpoint.GetOrCreateDbAsync(ct).ConfigureAwait(false);
        var collection = db.Collection(_endpoint.CollectionPath);

        Google.Cloud.Firestore.Query query = collection;

        if (_options.Where is not null)
            query = FirestoreQueryHelper.ApplyWhereFilters(query, _options.Where);
        if (_options.OrderBy is not null)
            query = FirestoreQueryHelper.ApplyOrderBy(query, _options.OrderBy);
        if (_options.Limit is not null)
            query = query.Limit(_options.Limit.Value);

        _listener = query.Listen(OnSnapshotAsync, _drain.ProcessingToken);

        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("Firestore consumer started: {Collection}", _endpoint.CollectionPath);
    }

    /// <inheritdoc />
    public async Task Stop(CancellationToken ct = default)
    {
        // 1. Stop accepting new snapshots
        if (_listener is not null)
            await _listener.StopAsync(ct).ConfigureAwait(false);

        // 2. Drain in-flight processing
        await _drain.DrainAsync(ct, _logger, $"fstore://{_endpoint.CollectionPath}").ConfigureAwait(false);

        _drain.Dispose();

        _logger?.LogInformation("Firestore consumer stopped: {Collection}", _endpoint.CollectionPath);
    }

    private async Task OnSnapshotAsync(QuerySnapshot snapshot, CancellationToken ct)
    {
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

            _endpoint.RecordMessageIn();

            await _processor.Process(exchange, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _endpoint.RecordError(ex);
            _logger?.LogError(ex, "Firestore change processing failed: {Collection}/{DocId}",
                _endpoint.CollectionPath, change.Document.Id);
        }
        finally
        {
            if (exchange is not null)
                await exchange.DisposeAsync().ConfigureAwait(false);
            _drain.Decrement();
        }
    }

}
