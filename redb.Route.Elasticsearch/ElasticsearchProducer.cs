using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Core.MSearch;
using Elastic.Clients.Elasticsearch.Core.Search;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.Elasticsearch;

/// <summary>
/// Elasticsearch producer — sends messages to Elasticsearch via Index/Bulk/Search/Get/Update/Delete/Count/Exists/MultiSearch.
/// Extends <see cref="ConnectableProducer"/> for persistent client lifecycle.
/// </summary>
internal sealed class ElasticsearchProducer : ConnectableProducer
{
    private readonly ElasticsearchEndpoint _endpoint;
    private readonly ElasticsearchEndpointOptions _options;
    private ElasticsearchClient? _client;

    /// <inheritdoc />
    protected override IEndpoint ProducerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ProducerName => $"es:{_endpoint.OperationType}:{_endpoint.IndexName}";

    internal ElasticsearchProducer(ElasticsearchEndpoint endpoint, ElasticsearchEndpointOptions options)
    {
        _endpoint = endpoint;
        _options = options;
    }

    /// <inheritdoc />
    protected override async Task ConnectAsync(CancellationToken ct)
    {
        _client = await _endpoint.GetOrCreateClientAsync(ct).ConfigureAwait(false);

        // Verify connectivity
        var ping = await _client.PingAsync(ct).ConfigureAwait(false);
        if (!ping.IsValidResponse)
            Logger?.LogWarning("Elasticsearch ping failed during connect: {Info}", ping.DebugInformation);
    }

    /// <inheritdoc />
    public override async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        EnsureStarted();
        ArgumentNullException.ThrowIfNull(exchange);

        // Allow runtime operation override via header
        var operation = _endpoint.OperationType;
        if (exchange.In.Headers.TryGetValue(ElasticsearchHeaders.Operation, out var opHeader) && opHeader is string opStr)
        {
            if (Enum.TryParse<ElasticsearchOperationType>(opStr, ignoreCase: true, out var headerOp))
                operation = headerOp;
        }

        using var activity = RouteTelemetryExtensions.StartTransportSpan(
            $"es {operation}", ActivityKind.Client,
            "db.system", "elasticsearch",
            _endpoint.Uri.NormalizedKey,
            destination: _endpoint.IndexName,
            operation: operation.ToString());

        await DispatchOperationAsync(operation, exchange, ct).ConfigureAwait(false);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  OPERATION DISPATCH
    // ═══════════════════════════════════════════════════════════════════

    private async Task DispatchOperationAsync(ElasticsearchOperationType operation, IExchange exchange,
        CancellationToken ct)
    {
        switch (operation)
        {
            case ElasticsearchOperationType.Index:
                await ProcessIndexAsync(exchange, ct).ConfigureAwait(false);
                break;
            case ElasticsearchOperationType.Bulk:
                await ProcessBulkAsync(exchange, ct).ConfigureAwait(false);
                break;
            case ElasticsearchOperationType.Search:
                await ProcessSearchAsync(exchange, ct).ConfigureAwait(false);
                break;
            case ElasticsearchOperationType.Get:
                await ProcessGetAsync(exchange, ct).ConfigureAwait(false);
                break;
            case ElasticsearchOperationType.Update:
                await ProcessUpdateAsync(exchange, ct).ConfigureAwait(false);
                break;
            case ElasticsearchOperationType.Delete:
                await ProcessDeleteAsync(exchange, ct).ConfigureAwait(false);
                break;
            case ElasticsearchOperationType.Count:
                await ProcessCountAsync(exchange, ct).ConfigureAwait(false);
                break;
            case ElasticsearchOperationType.Exists:
                await ProcessExistsAsync(exchange, ct).ConfigureAwait(false);
                break;
            case ElasticsearchOperationType.MultiSearch:
                await ProcessMultiSearchAsync(exchange, ct).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"Unsupported Elasticsearch operation: {operation}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  INDEX
    // ═══════════════════════════════════════════════════════════════════

    private async Task ProcessIndexAsync(IExchange exchange, CancellationToken ct)
    {
        var index = ResolveIndex(exchange);
        var documentId = ResolveDocumentId(exchange);
        var body = ResolveBodyAsJsonObject(exchange);

        var response = await _client!.IndexAsync(body, r =>
        {
            r.Index(index);
            if (documentId is not null) r.Id(documentId);
            var pipeline = ResolveStringOption(exchange, ElasticsearchHeaders.Pipeline, _options.Pipeline);
            if (!string.IsNullOrEmpty(pipeline)) r.Pipeline(pipeline);
            var routing = ResolveStringOption(exchange, ElasticsearchHeaders.Routing, _options.Routing);
            if (!string.IsNullOrEmpty(routing)) r.Routing(new Routing(routing));
            ApplyRefresh(r, exchange);
        }, ct).ConfigureAwait(false);

        ValidateResponse(response, "Index");

        exchange.In.Headers[ElasticsearchHeaders.DocumentId] = response.Id;
        exchange.In.Headers[ElasticsearchHeaders.Version] = response.Version;
        exchange.In.Headers[ElasticsearchHeaders.Result] = response.Result.ToString();
        exchange.In.Headers[ElasticsearchHeaders.SequenceNumber] = response.SeqNo;
        exchange.In.Headers[ElasticsearchHeaders.PrimaryTerm] = response.PrimaryTerm;
        _endpoint.RecordMessageOut();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  BULK
    // ═══════════════════════════════════════════════════════════════════

    private async Task ProcessBulkAsync(IExchange exchange, CancellationToken ct)
    {
        var index = ResolveIndex(exchange);
        var items = ResolveBodyAsList(exchange);

        int totalItems = 0;
        bool hasErrors = false;
        var errorDetails = new List<string>();

        // Batch into chunks of BulkSize
        foreach (var batch in items.Chunk(_options.BulkSize))
        {
            var response = await _client!.BulkAsync(b =>
            {
                b.Index(index);
                foreach (var item in batch)
                    b.Index(item, idx => { });

                var refresh = ResolveRefreshValue(exchange);
                if (refresh is not null) b.Refresh(refresh.Value);
            }, ct).ConfigureAwait(false);

            totalItems += response.Items.Count;
            if (response.Errors)
            {
                hasErrors = true;
                foreach (var item in response.Items.Where(i => i.Error is not null))
                    errorDetails.Add($"{item.Id}: {item.Error?.Reason}");
            }
        }

        exchange.In.Headers[ElasticsearchHeaders.BulkItemCount] = totalItems;
        exchange.In.Headers[ElasticsearchHeaders.BulkErrors] = hasErrors;
        if (hasErrors)
            exchange.In.Headers[ElasticsearchHeaders.BulkErrorItems] = JsonSerializer.Serialize(errorDetails);

        _endpoint.RecordMessageOut();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  SEARCH
    // ═══════════════════════════════════════════════════════════════════

    private async Task ProcessSearchAsync(IExchange exchange, CancellationToken ct)
    {
        var index = ResolveIndex(exchange);
        var query = exchange.In.Body as string ?? _options.Query;

        var response = await _client!.SearchAsync<JsonObject>(s =>
        {
            s.Indices(index);
            s.Size(_options.Size);
            s.TrackTotalHits(new TrackHits(_options.TrackTotalHits));

            ApplyQuery(s, query);
        }, ct).ConfigureAwait(false);

        ValidateResponse(response, "Search");

        var documents = response.Documents.ToList();
        exchange.Out = new Message { Body = documents, ContentType = "application/json" };
        exchange.Out.Headers[ElasticsearchHeaders.TotalHits] = response.Total;
        exchange.Pattern = ExchangePattern.InOut;
        _endpoint.RecordMessageOut();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  GET
    // ═══════════════════════════════════════════════════════════════════

    private async Task ProcessGetAsync(IExchange exchange, CancellationToken ct)
    {
        var index = ResolveIndex(exchange);
        var documentId = RequireDocumentId(exchange, "GET");

        var response = await _client!.GetAsync<JsonObject>(index, documentId, ct).ConfigureAwait(false);

        if (response.Found)
        {
            exchange.Out = new Message
            {
                Body = response.Source?.ToJsonString() ?? "{}",
                ContentType = "application/json"
            };
            exchange.Out.Headers[ElasticsearchHeaders.DocumentId] = response.Id;
            exchange.Out.Headers[ElasticsearchHeaders.Version] = response.Version;
            exchange.Out.Headers[ElasticsearchHeaders.SequenceNumber] = response.SeqNo;
            exchange.Out.Headers[ElasticsearchHeaders.PrimaryTerm] = response.PrimaryTerm;
        }
        else
        {
            exchange.Out = new Message { Body = null };
        }

        exchange.Pattern = ExchangePattern.InOut;
        _endpoint.RecordMessageOut();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  UPDATE
    // ═══════════════════════════════════════════════════════════════════

    private async Task ProcessUpdateAsync(IExchange exchange, CancellationToken ct)
    {
        var index = ResolveIndex(exchange);
        var documentId = RequireDocumentId(exchange, "UPDATE");
        var body = ResolveBodyAsJsonObject(exchange);

        var response = await _client!.UpdateAsync<JsonObject, JsonObject>(
            index, documentId, r =>
            {
                r.Doc(body);
                var refresh = ResolveRefreshValue(exchange);
                if (refresh is not null) r.Refresh(refresh.Value);
            }, ct).ConfigureAwait(false);

        ValidateResponse(response, "Update");

        exchange.In.Headers[ElasticsearchHeaders.Result] = response.Result.ToString();
        exchange.In.Headers[ElasticsearchHeaders.Version] = response.Version;
        _endpoint.RecordMessageOut();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  DELETE
    // ═══════════════════════════════════════════════════════════════════

    private async Task ProcessDeleteAsync(IExchange exchange, CancellationToken ct)
    {
        var index = ResolveIndex(exchange);
        var documentId = RequireDocumentId(exchange, "DELETE");

        var request = new DeleteRequest(index, documentId);
        var refresh = ResolveRefreshValue(exchange);
        if (refresh is not null) request.Refresh = refresh.Value;

        var response = await _client!.DeleteAsync(request, ct).ConfigureAwait(false);

        ValidateResponse(response, "Delete");

        exchange.In.Headers[ElasticsearchHeaders.Result] = response.Result.ToString();
        _endpoint.RecordMessageOut();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  COUNT
    // ═══════════════════════════════════════════════════════════════════

    private async Task ProcessCountAsync(IExchange exchange, CancellationToken ct)
    {
        var index = ResolveIndex(exchange);
        var query = exchange.In.Body as string ?? _options.Query;

        var response = await _client!.CountAsync(r =>
        {
            r.Indices(index);
            ApplyQuery(r, query);
        }, ct).ConfigureAwait(false);

        ValidateResponse(response, "Count");

        exchange.Out = new Message { Body = response.Count };
        exchange.Pattern = ExchangePattern.InOut;
        _endpoint.RecordMessageOut();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  EXISTS
    // ═══════════════════════════════════════════════════════════════════

    private async Task ProcessExistsAsync(IExchange exchange, CancellationToken ct)
    {
        var index = ResolveIndex(exchange);
        var documentId = RequireDocumentId(exchange, "EXISTS");

        var response = await _client!.ExistsAsync(index, documentId, ct).ConfigureAwait(false);

        exchange.Out = new Message { Body = response.Exists };
        exchange.Pattern = ExchangePattern.InOut;
        _endpoint.RecordMessageOut();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  MULTISEARCH
    // ═══════════════════════════════════════════════════════════════════

    private async Task ProcessMultiSearchAsync(IExchange exchange, CancellationToken ct)
    {
        var defaultIndex = ResolveIndex(exchange);
        var queries = ResolveBodyAsQueryArray(exchange);

        var items = new SearchRequestItem[queries.Count];
        for (int i = 0; i < queries.Count; i++)
        {
            var q = queries[i];
            var header = new MultisearchHeader();
            if (q.TryGetPropertyValue("index", out var idxNode) && idxNode is not null)
                header.Indices = idxNode.GetValue<string>();

            var body = new MultisearchBody();

            // Query (required) — raw JSON query passed via WrapperQuery
            if (q.TryGetPropertyValue("query", out var queryNode) && queryNode is not null)
            {
                var raw = queryNode.ToJsonString();
                body.Query = new Query
                {
                    Wrapper = new WrapperQuery
                    {
                        Query = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
                    }
                };
            }

            if (q.TryGetPropertyValue("size", out var sizeNode) && sizeNode is not null)
                body.Size = sizeNode.GetValue<int>();
            if (q.TryGetPropertyValue("from", out var fromNode) && fromNode is not null)
                body.From = fromNode.GetValue<int>();

            // Sort: "field:order,field2:order2" string format (same as consumer)
            if (q.TryGetPropertyValue("sort", out var sortNode) && sortNode is not null)
            {
                var sortStr = sortNode.GetValue<string>();
                var sortParts = sortStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var sortOptions = new List<SortOptions>();
                foreach (var part in sortParts)
                {
                    var fieldOrder = part.Split(':', 2);
                    var fieldName = fieldOrder[0].Trim();
                    var order = fieldOrder.Length > 1 &&
                                fieldOrder[1].Trim().Equals("desc", StringComparison.OrdinalIgnoreCase)
                        ? SortOrder.Desc
                        : SortOrder.Asc;
                    sortOptions.Add(new SortOptions { Field = new FieldSort { Field = fieldName, Order = order } });
                }
                body.Sort = sortOptions;
            }

            // Source filtering: "_source" can be false (disable) or "field1,field2" (includes only)
            if (q.TryGetPropertyValue("_source", out var sourceNode) && sourceNode is not null)
            {
                if (sourceNode is JsonValue jv && jv.TryGetValue<bool>(out var srcBool))
                {
                    body.Source = new SourceConfig(srcBool);
                }
                else
                {
                    var fields = sourceNode.GetValue<string>()
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(f => (Field)f).ToArray();
                    body.Source = new SourceConfig(new Elastic.Clients.Elasticsearch.Core.Search.SourceFilter { Includes = fields });
                }
            }

            items[i] = new SearchRequestItem(header, body);
        }

        var response = await _client!.MultiSearchAsync<JsonObject>(ms =>
        {
            ms.Indices(defaultIndex);
            ms.Searches(items);
        }, ct).ConfigureAwait(false);

        ValidateResponse(response, "MultiSearch");

        var allResults = new List<List<JsonObject>>();
        var totalHitsList = new List<long>();
        bool hasErrors = false;

        foreach (var item in response.Responses)
        {
            if (item.Tag == UnionTag.T1)
            {
                var searchItem = item.Value1!;
                allResults.Add(searchItem.Documents.ToList());
                totalHitsList.Add(searchItem.Total);
            }
            else if (item.Tag == UnionTag.T2)
            {
                hasErrors = true;
                allResults.Add(new List<JsonObject>());
                totalHitsList.Add(0);
            }
        }

        exchange.Out = new Message { Body = allResults, ContentType = "application/json" };
        exchange.Out.Headers[ElasticsearchHeaders.MultiSearchResponseCount] = allResults.Count;
        exchange.Out.Headers[ElasticsearchHeaders.MultiSearchTotalHits] = totalHitsList.ToArray();
        exchange.Out.Headers[ElasticsearchHeaders.MultiSearchHasErrors] = hasErrors;
        exchange.Pattern = ExchangePattern.InOut;
        _endpoint.RecordMessageOut();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════════════════════

    private string ResolveIndex(IExchange exchange)
    {
        if (exchange.In.Headers.TryGetValue(ElasticsearchHeaders.IndexName, out var hdr) && hdr is string idx &&
            !string.IsNullOrEmpty(idx))
            return idx;
        return _endpoint.IndexName;
    }

    private string? ResolveDocumentId(IExchange exchange)
    {
        if (exchange.In.Headers.TryGetValue(ElasticsearchHeaders.DocumentId, out var hdr) && hdr is string id &&
            !string.IsNullOrEmpty(id))
            return id;
        return _options.DocumentId?.Resolve(exchange);
    }

    private static string RequireDocumentId(IExchange exchange, string operation)
    {
        if (exchange.In.Headers.TryGetValue(ElasticsearchHeaders.DocumentId, out var hdr) && hdr is string id &&
            !string.IsNullOrEmpty(id))
            return id;
        throw new InvalidOperationException($"ElasticsearchHeaders.DocumentId is required for {operation} operation");
    }

    private static string ResolveStringOption(IExchange exchange, string headerKey, string defaultValue)
    {
        if (exchange.In.Headers.TryGetValue(headerKey, out var hdr) && hdr is string val &&
            !string.IsNullOrEmpty(val))
            return val;
        return defaultValue;
    }

    private static JsonObject ResolveBodyAsJsonObject(IExchange exchange)
    {
        return exchange.In.Body switch
        {
            JsonObject jo => jo,
            string s => JsonNode.Parse(s)?.AsObject()
                        ?? throw new InvalidOperationException("Body is not valid JSON"),
            byte[] bytes => JsonNode.Parse(bytes)?.AsObject()
                            ?? throw new InvalidOperationException("Body is not valid JSON"),
            null => throw new InvalidOperationException("Body is null — cannot index empty document"),
            _ => JsonNode.Parse(JsonSerializer.Serialize(exchange.In.Body))?.AsObject()
                 ?? throw new InvalidOperationException("Cannot serialize body to JSON"),
        };
    }

    private static List<JsonObject> ResolveBodyAsList(IExchange exchange)
    {
        return exchange.In.Body switch
        {
            IEnumerable<JsonObject> list => list.ToList(),
            string s => JsonSerializer.Deserialize<List<JsonObject>>(s)
                        ?? throw new InvalidOperationException("Body is not a valid JSON array"),
            byte[] bytes => JsonSerializer.Deserialize<List<JsonObject>>(bytes)
                            ?? throw new InvalidOperationException("Body is not a valid JSON array"),
            null => throw new InvalidOperationException("Body is null — cannot bulk index empty list"),
            _ => throw new InvalidOperationException(
                $"Bulk operation requires body of type IEnumerable<JsonObject>, string, or byte[], got {exchange.In.Body.GetType().Name}"),
        };
    }

    private static List<JsonObject> ResolveBodyAsQueryArray(IExchange exchange)
    {
        return exchange.In.Body switch
        {
            string s => JsonSerializer.Deserialize<List<JsonObject>>(s)
                        ?? throw new InvalidOperationException("MultiSearch body is not a valid JSON array"),
            byte[] bytes => JsonSerializer.Deserialize<List<JsonObject>>(bytes)
                            ?? throw new InvalidOperationException("MultiSearch body is not a valid JSON array"),
            IEnumerable<JsonObject> list => list.ToList(),
            null => throw new InvalidOperationException("Body is null — MultiSearch requires a JSON array of queries"),
            _ => throw new InvalidOperationException(
                $"MultiSearch requires body of type string, byte[], or IEnumerable<JsonObject>, got {exchange.In.Body.GetType().Name}"),
        };
    }

    private Refresh? ResolveRefreshValue(IExchange exchange)
    {
        var refresh = ResolveStringOption(exchange, ElasticsearchHeaders.Refresh, _options.Refresh);
        if (string.IsNullOrEmpty(refresh)) return null;

        return refresh.ToLowerInvariant() switch
        {
            "true" => Refresh.True,
            "wait_for" => Refresh.WaitFor,
            _ => Refresh.False,
        };
    }

    private void ApplyRefresh(IndexRequestDescriptor<JsonObject> r, IExchange exchange)
    {
        var v = ResolveRefreshValue(exchange);
        if (v is not null) r.Refresh(v.Value);
    }

    /// <summary>Applies a raw JSON query via WrapperQuery (base64-encoded JSON).</summary>
    private static void ApplyQuery(SearchRequestDescriptor<JsonObject> s, string? query)
    {
        if (!string.IsNullOrEmpty(query))
            s.Query(q => q.Wrapper(w => w.Query(Convert.ToBase64String(Encoding.UTF8.GetBytes(query)))));
    }

    /// <summary>Applies a raw JSON query via WrapperQuery (base64-encoded JSON) for count requests.</summary>
    private static void ApplyQuery(CountRequestDescriptor r, string? query)
    {
        if (!string.IsNullOrEmpty(query))
            r.Query(q => q.Wrapper(w => w.Query(Convert.ToBase64String(Encoding.UTF8.GetBytes(query)))));
    }

    private static void ValidateResponse(Elastic.Transport.Products.Elasticsearch.ElasticsearchResponse response, string operation)
    {
        if (!response.IsValidResponse)
            throw new InvalidOperationException(
                $"Elasticsearch {operation} failed: {response.DebugInformation}");
    }
}
