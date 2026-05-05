using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Core.Search;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Elasticsearch;

/// <summary>
/// Elasticsearch consumer — polls an index using the Search API (or Scroll API for large result sets).
/// Extends <see cref="DrainableConsumer"/> for graceful two-CTS shutdown.
/// <para>
/// Supports search_after deep pagination, scroll API (legacy),
/// source filtering, sort ordering, delete-after-read, and query DSL.
/// </para>
/// </summary>
internal sealed class ElasticsearchConsumer : DrainableConsumer
{
    private readonly ElasticsearchEndpoint _endpoint;
    private readonly ElasticsearchEndpointOptions _options;
    private ElasticsearchClient? _client;

    // search_after state: sort values from last hit of previous poll
    private IReadOnlyCollection<FieldValue>? _lastSortValues;

    /// <inheritdoc />
    protected override IEndpoint ConsumerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ConsumerName => $"es://{_endpoint.IndexName}";

    internal ElasticsearchConsumer(ElasticsearchEndpoint endpoint, IProcessor processor,
        ElasticsearchEndpointOptions options)
        : base(processor)
    {
        _endpoint = endpoint;
        _options = options;
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
                if (!string.IsNullOrEmpty(_options.ScrollTimeout))
                    await PollWithScrollAsync(processingCt).ConfigureAwait(false);
                else
                    await PollWithSearchAsync(processingCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (pollCt.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Elasticsearch consumer poll error on index {Index}", _endpoint.IndexName);
                _endpoint.RecordError();
            }

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
    //  SEARCH (standard pagination via search_after)
    // ═══════════════════════════════════════════════════════════════════

    private async Task PollWithSearchAsync(CancellationToken ct)
    {
        var response = await _client!.SearchAsync<JsonObject>(s =>
        {
            s.Indices(_endpoint.IndexName);
            s.Size(_options.Size);
            s.TrackTotalHits(new TrackHits(_options.TrackTotalHits));

            ConfigureQuery(s);
            ConfigureSort(s);
            ConfigureSourceFilter(s);

            if (_lastSortValues is { Count: > 0 })
                s.SearchAfter(_lastSortValues.ToList());
        }, ct).ConfigureAwait(false);

        if (!response.IsValidResponse)
        {
            Logger?.LogWarning("Elasticsearch search failed: {Info}", response.DebugInformation);
            return;
        }

        await ProcessHitsAsync(response.Hits, ct).ConfigureAwait(false);

        // Update search_after cursor from last hit
        if (response.Hits.Count > 0)
        {
            var lastHit = response.Hits.Last();
            _lastSortValues = lastHit.Sort;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  SCROLL (for large result sets — deprecated in ES 8.x but supported)
    // ═══════════════════════════════════════════════════════════════════

    private async Task PollWithScrollAsync(CancellationToken ct)
    {
        // Initial scroll search
        var searchResponse = await _client!.SearchAsync<JsonObject>(s =>
        {
            s.Indices(_endpoint.IndexName);
            s.Size(_options.Size);
            s.Scroll(_options.ScrollTimeout);
            s.TrackTotalHits(new TrackHits(_options.TrackTotalHits));

            ConfigureQuery(s);
            ConfigureSort(s);
            ConfigureSourceFilter(s);
        }, ct).ConfigureAwait(false);

        if (!searchResponse.IsValidResponse)
        {
            Logger?.LogWarning("Elasticsearch scroll search failed: {Info}", searchResponse.DebugInformation);
            return;
        }

        string? scrollId = searchResponse.ScrollId?.ToString();

        try
        {
            // Process first page
            await ProcessHitsAsync(searchResponse.Hits, ct).ConfigureAwait(false);
            int lastHitCount = searchResponse.Hits.Count;

            // Continue scrolling while there are hits
            while (lastHitCount > 0 && !ct.IsCancellationRequested && scrollId is not null)
            {
                var scrollResponse = await _client.ScrollAsync<JsonObject>(s =>
                {
                    s.ScrollId(scrollId);
                    s.Scroll(_options.ScrollTimeout);
                }, ct).ConfigureAwait(false);

                if (!scrollResponse.IsValidResponse || scrollResponse.Hits.Count == 0)
                    break;

                scrollId = scrollResponse.ScrollId?.ToString();
                await ProcessHitsAsync(scrollResponse.Hits, ct).ConfigureAwait(false);
                lastHitCount = scrollResponse.Hits.Count;
            }
        }
        finally
        {
            // Clear scroll context
            if (scrollId is not null)
            {
                try
                {
                    await _client.ClearScrollAsync(s => s.ScrollId(new[] { scrollId }), ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger?.LogDebug(ex, "Failed to clear scroll context {ScrollId}", scrollId);
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  HIT PROCESSING
    // ═══════════════════════════════════════════════════════════════════

    private async Task ProcessHitsAsync(IReadOnlyCollection<Hit<JsonObject>> hits, CancellationToken ct)
    {
        foreach (var hit in hits)
        {
            if (ct.IsCancellationRequested) break;

            // Create exchange with document body
            var bodyJson = hit.Source?.ToJsonString() ?? "{}";
            var message = new Message { Body = bodyJson, ContentType = "application/json" };
            var exchange = new Exchange(message);

            // Set headers
            var headers = exchange.In.Headers;
            headers[ElasticsearchHeaders.IndexName] = hit.Index;
            headers[ElasticsearchHeaders.DocumentId] = hit.Id;

            if (hit.Version is not null)
                headers[ElasticsearchHeaders.Version] = hit.Version;
            if (hit.SeqNo is not null)
                headers[ElasticsearchHeaders.SequenceNumber] = hit.SeqNo;
            if (hit.PrimaryTerm is not null)
                headers[ElasticsearchHeaders.PrimaryTerm] = hit.PrimaryTerm;
            if (hit.Score is not null)
                headers[ElasticsearchHeaders.Score] = hit.Score;
            if (hit.Sort is { Count: > 0 })
                headers[ElasticsearchHeaders.SortValues] = JsonSerializer.Serialize(hit.Sort);

            _endpoint.RecordMessageIn();
            _endpoint.RecordBytesIn(System.Text.Encoding.UTF8.GetByteCount(bodyJson));

            await ProcessWithTracking(exchange, ct).ConfigureAwait(false);

            // Post-processing: delete after read
            if (_options.DeleteAfterRead && hit.Id is not null &&
                (exchange.Exception == null || exchange.ExceptionHandled))
            {
                await _client!.DeleteAsync(_endpoint.IndexName, hit.Id, ct).ConfigureAwait(false);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  QUERY / SORT / SOURCE CONFIGURATION
    // ═══════════════════════════════════════════════════════════════════

    private void ConfigureQuery(SearchRequestDescriptor<JsonObject> s)
    {
        if (!string.IsNullOrEmpty(_options.Query))
        {
            // Raw JSON query via WrapperQuery (base64-encoded)
            s.Query(q => q.Wrapper(w =>
                w.Query(Convert.ToBase64String(Encoding.UTF8.GetBytes(_options.Query)))));
        }
        // If no query specified, default is match_all (ES behavior)
    }

    private void ConfigureSort(SearchRequestDescriptor<JsonObject> s)
    {
        if (!string.IsNullOrEmpty(_options.Sort))
        {
            // Parse "field:order,field2:order2" format
            var sortParts = _options.Sort.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            s.Sort(sortBuilder =>
            {
                foreach (var part in sortParts)
                {
                    var fieldOrder = part.Split(':', 2);
                    var fieldName = fieldOrder[0].Trim();
                    var order = fieldOrder.Length > 1 &&
                                fieldOrder[1].Trim().Equals("desc", StringComparison.OrdinalIgnoreCase)
                        ? SortOrder.Desc
                        : SortOrder.Asc;

                    sortBuilder.Field(fieldName, f => f.Order(order));
                }
            });
        }
        else
        {
            // Default sort for search_after pagination
            s.Sort(sortBuilder => sortBuilder.Field("_doc", f => f.Order(SortOrder.Asc)));
        }
    }

    private void ConfigureSourceFilter(SearchRequestDescriptor<JsonObject> s)
    {
        var hasIncludes = !string.IsNullOrEmpty(_options.SourceIncludes);
        var hasExcludes = !string.IsNullOrEmpty(_options.SourceExcludes);

        if (!hasIncludes && !hasExcludes) return;

        s.Source(new SourceConfig(new SourceFilter
        {
            Includes = hasIncludes
                ? _options.SourceIncludes!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(f => (Elastic.Clients.Elasticsearch.Field)f).ToArray()
                : null,
            Excludes = hasExcludes
                ? _options.SourceExcludes!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(f => (Elastic.Clients.Elasticsearch.Field)f).ToArray()
                : null,
        }));
    }
}
