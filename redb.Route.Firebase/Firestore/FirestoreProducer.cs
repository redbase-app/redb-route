using System.Diagnostics;
using System.Text.Json;
using Google.Cloud.Firestore;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.Firebase;

/// <summary>
/// Firestore producer — performs CRUD and query operations on Firestore collections.
/// Dispatches by <see cref="FirestoreOperationType"/>: Set, Get, Update, Delete, Query, BatchWrite.
/// </summary>
internal sealed class FirestoreProducer : ConnectableProducer
{
    private readonly FirestoreEndpoint _endpoint;
    private readonly FirestoreEndpointOptions _options;
    private FirestoreDb? _db;

    /// <inheritdoc />
    protected override IEndpoint ProducerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ProducerName => _endpoint.Uri.NormalizedKey;

    internal FirestoreProducer(FirestoreEndpoint endpoint, FirestoreEndpointOptions options)
    {
        _endpoint = endpoint;
        _options = options;
    }

    /// <inheritdoc />
    protected override async Task ConnectAsync(CancellationToken ct)
    {
        _db = await _endpoint.GetOrCreateDbAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        EnsureStarted();

        using var activity = RouteTelemetryExtensions.StartTransportSpan(
            $"firestore {_options.Operation}", ActivityKind.Client,
            "db.system", "firestore",
            _endpoint.Uri.NormalizedKey,
            destination: _endpoint.CollectionPath,
            operation: _options.Operation.ToString());

        var collection = _db!.Collection(_endpoint.CollectionPath);

        switch (_options.Operation)
        {
            case FirestoreOperationType.Set:
                await ProcessSet(exchange, collection, ct).ConfigureAwait(false);
                break;
            case FirestoreOperationType.Get:
                await ProcessGet(exchange, collection, ct).ConfigureAwait(false);
                break;
            case FirestoreOperationType.Update:
                await ProcessUpdate(exchange, collection, ct).ConfigureAwait(false);
                break;
            case FirestoreOperationType.Delete:
                await ProcessDelete(exchange, collection, ct).ConfigureAwait(false);
                break;
            case FirestoreOperationType.Query:
                await ProcessQuery(exchange, collection, ct).ConfigureAwait(false);
                break;
            case FirestoreOperationType.BatchWrite:
                await ProcessBatchWrite(exchange, collection, ct).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"Unknown Firestore operation: {_options.Operation}");
        }
    }

    private async Task ProcessSet(IExchange exchange, CollectionReference collection, CancellationToken ct)
    {
        var docId = ResolveDocumentId(exchange);
        var data = ResolveDocumentData(exchange);

        DocumentReference docRef = docId is not null
            ? collection.Document(docId)
            : collection.Document(); // auto-generate ID

        WriteResult writeResult;
        if (_options.Merge)
            writeResult = await docRef.SetAsync(data, SetOptions.MergeAll, ct).ConfigureAwait(false);
        else
            writeResult = await docRef.SetAsync(data, cancellationToken: ct).ConfigureAwait(false);

        exchange.In.Headers[FirestoreHeaders.DocumentId] = docRef.Id;
        exchange.In.Headers[FirestoreHeaders.DocumentPath] = docRef.Path;
        exchange.In.Headers[FirestoreHeaders.WriteTime] = writeResult.UpdateTime;
        _endpoint.RecordMessageOut();
    }

    private async Task ProcessGet(IExchange exchange, CollectionReference collection, CancellationToken ct)
    {
        var docId = ResolveDocumentId(exchange)
                    ?? throw new InvalidOperationException("DocumentId is required for Get operation");

        var snapshot = await collection.Document(docId).GetSnapshotAsync(ct).ConfigureAwait(false);

        if (!snapshot.Exists)
        {
            exchange.Out = new Message(null);
        }
        else
        {
            var data = snapshot.ToDictionary();
            exchange.Out = _options.RawJson
                ? new Message(JsonSerializer.Serialize(data))
                : new Message(data);
            exchange.Out.Headers[FirestoreHeaders.DocumentId] = snapshot.Id;
            exchange.Out.Headers[FirestoreHeaders.CreateTime] = snapshot.CreateTime;
            exchange.Out.Headers[FirestoreHeaders.UpdateTime] = snapshot.UpdateTime;
        }

        exchange.Pattern = ExchangePattern.InOut;
        _endpoint.RecordMessageIn();
    }

    private async Task ProcessUpdate(IExchange exchange, CollectionReference collection, CancellationToken ct)
    {
        var docId = ResolveDocumentId(exchange)
                    ?? throw new InvalidOperationException("DocumentId is required for Update operation");

        var data = ResolveDocumentData(exchange);
        var writeResult = await collection.Document(docId).UpdateAsync(data, cancellationToken: ct).ConfigureAwait(false);

        exchange.In.Headers[FirestoreHeaders.WriteTime] = writeResult.UpdateTime;
        _endpoint.RecordMessageOut();
    }

    private async Task ProcessDelete(IExchange exchange, CollectionReference collection, CancellationToken ct)
    {
        var docId = ResolveDocumentId(exchange)
                    ?? throw new InvalidOperationException("DocumentId is required for Delete operation");

        var writeResult = await collection.Document(docId).DeleteAsync(cancellationToken: ct).ConfigureAwait(false);

        exchange.In.Headers[FirestoreHeaders.WriteTime] = writeResult.UpdateTime;
        _endpoint.RecordMessageOut();
    }

    private async Task ProcessQuery(IExchange exchange, CollectionReference collection, CancellationToken ct)
    {
        Google.Cloud.Firestore.Query query = collection;

        if (_options.Where is not null)
            query = FirestoreQueryHelper.ApplyWhereFilters(query, _options.Where);
        if (_options.OrderBy is not null)
            query = FirestoreQueryHelper.ApplyOrderBy(query, _options.OrderBy);
        if (_options.Offset is not null)
            query = query.Offset(_options.Offset.Value);
        if (_options.Limit is not null)
            query = query.Limit(_options.Limit.Value);

        var snapshot = await query.GetSnapshotAsync(ct).ConfigureAwait(false);
        var docs = snapshot.Documents.Select(d => d.ToDictionary()).ToList();

        exchange.Out = _options.RawJson
            ? new Message(JsonSerializer.Serialize(docs))
            : new Message(docs);
        exchange.Out.Headers[FirestoreHeaders.DocumentCount] = snapshot.Count;
        exchange.Pattern = ExchangePattern.InOut;
        _endpoint.RecordMessageIn();
    }

    private async Task ProcessBatchWrite(IExchange exchange, CollectionReference collection, CancellationToken ct)
    {
        const int maxBatchSize = 500; // Firestore hard limit per batch

        var items = exchange.In.Body as IEnumerable<IDictionary<string, object?>>
                    ?? throw new InvalidOperationException(
                        "BatchWrite requires IEnumerable<IDictionary<string, object?>> body");

        var totalCount = 0;
        var batchCount = 0;
        WriteBatch? batch = null;

        foreach (var item in items)
        {
            batch ??= _db!.StartBatch();
            var docRef = collection.Document(); // auto-ID
            if (_options.Merge)
                batch.Set(docRef, item, SetOptions.MergeAll);
            else
                batch.Set(docRef, item);
            totalCount++;
            batchCount++;

            if (batchCount >= maxBatchSize)
            {
                await batch.CommitAsync(ct).ConfigureAwait(false);
                batch = null;
                batchCount = 0;
            }
        }

        if (batch is not null && batchCount > 0)
            await batch.CommitAsync(ct).ConfigureAwait(false);

        exchange.In.Headers[FirestoreHeaders.DocumentCount] = totalCount;
        _endpoint.RecordMessageOut();
    }

    // ── Helpers ──

    private string? ResolveDocumentId(IExchange exchange)
    {
        return exchange.In.GetHeader<string>(FirestoreHeaders.DocumentId)
               ?? _options.DocumentId?.Resolve(exchange);
    }

    private static IDictionary<string, object?> ResolveDocumentData(IExchange exchange)
    {
        if (exchange.In.Body is IDictionary<string, object?> dictNullable)
            return dictNullable;
        if (exchange.In.Body is IDictionary<string, object> dict)
            return dict.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        if (exchange.In.Body is string json)
        {
            // Deserialize to JsonElement first, then recursively convert to native types.
            // Direct Deserialize<Dictionary<string, object?>> produces JsonElement values
            // which Firestore SDK cannot serialize.
            var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
                      ?? throw new InvalidOperationException("Failed to deserialize JSON body to dictionary");
            return ConvertJsonElements(raw);
        }
        throw new InvalidOperationException(
            $"Firestore Set/Update requires IDictionary<string, object?> or JSON string body, got {exchange.In.Body?.GetType().Name ?? "null"}");
    }

    private static IDictionary<string, object?> ConvertJsonElements(Dictionary<string, JsonElement> raw)
    {
        var result = new Dictionary<string, object?>(raw.Count);
        foreach (var (key, value) in raw)
            result[key] = ConvertJsonElement(value);
        return result;
    }

    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when element.TryGetInt64(out var l) => l,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToList(),
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(p => p.Name, p => ConvertJsonElement(p.Value)),
            _ => element.GetRawText()
        };
    }


}
