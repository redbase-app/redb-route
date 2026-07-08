using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elastic.Clients.Elasticsearch;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Elasticsearch;
using Xunit.Abstractions;

namespace redb.Route.Tests.Elasticsearch;

/// <summary>
/// Integration tests against Elasticsearch docker container.
/// Expects Elasticsearch at localhost:9200 (no auth, security disabled).
/// Start with: docker compose -f docker-compose.tests.yml up elasticsearch -d
/// </summary>
[Trait("Category", "Integration")]
public sealed class ElasticsearchIntegrationTests : IAsyncLifetime
{
    private const string Nodes = "http://localhost:9200";
    private readonly ITestOutputHelper _output;
    private ElasticsearchClient? _rawClient;

    public ElasticsearchIntegrationTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        _rawClient = new ElasticsearchClient(new ElasticsearchClientSettings(new Uri(Nodes)));
        // Verify ES is reachable
        var ping = await _rawClient.PingAsync();
        if (!ping.IsValidResponse)
            throw new InvalidOperationException($"Elasticsearch not reachable at {Nodes}: {ping.DebugInformation}");
    }

    public async Task DisposeAsync()
    {
        // Delete all test-* indices
        if (_rawClient is not null)
        {
            await _rawClient.Indices.DeleteAsync("test-*");
        }
    }

    // ───── Helpers ─────

    private static string UniqueIndex() => $"test-{Guid.NewGuid():N}";

    private ElasticsearchEndpoint CreateEndpoint(string index, string? extraParams = null)
    {
        var qs = $"nodes={Nodes}";
        if (extraParams is not null) qs += $"&{extraParams}";
        var uri = EndpointUriParser.Parse($"elasticsearch://{index}?{qs}");
        return (ElasticsearchEndpoint)new ElasticsearchComponent().CreateEndpoint(uri);
    }

    private ElasticsearchEndpoint CreateEndpointWithOp(ElasticsearchOperationType op, string index, string? extraParams = null)
    {
        var qs = $"nodes={Nodes}";
        if (extraParams is not null) qs += $"&{extraParams}";
        var uri = EndpointUriParser.Parse($"elasticsearch://{op}:{index}?{qs}");
        return (ElasticsearchEndpoint)new ElasticsearchComponent().CreateEndpoint(uri);
    }

    private async Task IndexDocument(string index, string id, object doc)
    {
        var response = await _rawClient!.IndexAsync(doc, i => i.Index(index).Id(id).Refresh(Refresh.WaitFor));
        if (!response.IsValidResponse)
            throw new InvalidOperationException($"Failed to index doc: {response.DebugInformation}");
    }

    private async Task IndexDocumentJson(string index, string id, string json)
    {
        var doc = JsonSerializer.Deserialize<JsonObject>(json);
        await IndexDocument(index, id, doc!);
    }

    private async Task RefreshIndex(string index)
    {
        await _rawClient!.Indices.RefreshAsync(index);
    }

    private async Task<long> CountDocuments(string index)
    {
        var response = await _rawClient!.CountAsync(c => c.Indices(index));
        return response.IsValidResponse ? response.Count : 0;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  INDEX + GET ROUNDTRIP
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Index_Get_Roundtrip()
    {
        var index = UniqueIndex();
        _output.WriteLine($"Index: {index}");

        // Index a document
        var ep = CreateEndpoint(index, "refresh=wait_for");
        var producer = ep.CreateProducer();
        await producer.Start();

        var body = new JsonObject { ["title"] = "Test Doc", ["content"] = "Hello Elasticsearch!" };
        var exchange = new Exchange(new Message(body.ToJsonString()));
        exchange.In.Headers[ElasticsearchHeaders.DocumentId] = "doc-1";
        await producer.Process(exchange);

        // Verify index headers
        exchange.In.Headers[ElasticsearchHeaders.DocumentId].Should().Be("doc-1");
        exchange.In.Headers[ElasticsearchHeaders.Result].Should().NotBeNull();
        exchange.In.Headers[ElasticsearchHeaders.Version].Should().NotBeNull();

        // Get the document back
        var getEp = CreateEndpointWithOp(ElasticsearchOperationType.Get, index);
        var getProducer = getEp.CreateProducer();
        await getProducer.Start();

        var getExchange = new Exchange(new Message());
        getExchange.In.Headers[ElasticsearchHeaders.DocumentId] = "doc-1";
        await getProducer.Process(getExchange);

        getExchange.Pattern.Should().Be(ExchangePattern.InOut);
        getExchange.Out.Should().NotBeNull();
        var resultBody = getExchange.Out!.Body?.ToString();
        resultBody.Should().Contain("Test Doc");
        resultBody.Should().Contain("Hello Elasticsearch!");

        await getProducer.Stop();
        await producer.Stop();
    }

    [Fact]
    public async Task Index_WithAutoId_ReturnsGeneratedId()
    {
        var index = UniqueIndex();

        var ep = CreateEndpoint(index, "refresh=wait_for");
        var producer = ep.CreateProducer();
        await producer.Start();

        var body = new JsonObject { ["title"] = "Auto ID Doc" };
        var exchange = new Exchange(new Message(body.ToJsonString()));
        // No explicit DocumentId header — ES auto-generates
        await producer.Process(exchange);

        var docId = exchange.In.Headers[ElasticsearchHeaders.DocumentId] as string;
        docId.Should().NotBeNullOrEmpty("Elasticsearch should auto-generate an ID");
        _output.WriteLine($"Auto-generated ID: {docId}");

        await producer.Stop();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  INDEX + SEARCH ROUNDTRIP
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Index_Search_Roundtrip()
    {
        var index = UniqueIndex();

        // Index multiple documents
        var ep = CreateEndpoint(index, "refresh=wait_for");
        var producer = ep.CreateProducer();
        await producer.Start();

        for (int i = 0; i < 3; i++)
        {
            var doc = new JsonObject { ["title"] = $"Doc {i}", ["seq"] = i };
            var ex = new Exchange(new Message(doc.ToJsonString()));
            ex.In.Headers[ElasticsearchHeaders.DocumentId] = $"doc-{i}";
            await producer.Process(ex);
        }
        await producer.Stop();

        // Search for all documents
        var searchEp = CreateEndpointWithOp(ElasticsearchOperationType.Search, index);
        var searchProducer = searchEp.CreateProducer();
        await searchProducer.Start();

        var searchExchange = new Exchange(new Message("{\"match_all\":{}}"));
        await searchProducer.Process(searchExchange);

        searchExchange.Pattern.Should().Be(ExchangePattern.InOut);
        searchExchange.Out.Should().NotBeNull();

        var totalHits = searchExchange.Out!.Headers[ElasticsearchHeaders.TotalHits];
        totalHits.Should().NotBeNull();
        ((long)totalHits!).Should().Be(3);

        await searchProducer.Stop();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  BULK
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Bulk_IndexMultiple_AllIndexed()
    {
        var index = UniqueIndex();

        var ep = CreateEndpointWithOp(ElasticsearchOperationType.Bulk, index, "refresh=wait_for&bulkSize=20");
        var producer = ep.CreateProducer();
        await producer.Start();

        // Create a list of documents
        var docs = new JsonArray();
        for (int i = 0; i < 50; i++)
        {
            docs.Add(new JsonObject { ["title"] = $"Bulk Doc {i}", ["seq"] = i });
        }

        var exchange = new Exchange(new Message(docs.ToJsonString()));
        await producer.Process(exchange);

        exchange.In.Headers[ElasticsearchHeaders.BulkItemCount].Should().NotBeNull();
        ((int)exchange.In.Headers[ElasticsearchHeaders.BulkItemCount]!).Should().Be(50);

        var hasErrors = exchange.In.Headers[ElasticsearchHeaders.BulkErrors];
        ((bool)hasErrors!).Should().BeFalse();

        // Verify count
        await RefreshIndex(index);
        var count = await CountDocuments(index);
        count.Should().Be(50);

        await producer.Stop();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  UPDATE
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_PartialDocument_Merges()
    {
        var index = UniqueIndex();

        // Index a document first
        var ep = CreateEndpoint(index, "refresh=wait_for");
        var producer = ep.CreateProducer();
        await producer.Start();

        var body = new JsonObject { ["title"] = "Original", ["author"] = "Alice", ["views"] = 0 };
        var indexExchange = new Exchange(new Message(body.ToJsonString()));
        indexExchange.In.Headers[ElasticsearchHeaders.DocumentId] = "upd-1";
        await producer.Process(indexExchange);
        await producer.Stop();

        // Update only the title and views
        var updateEp = CreateEndpointWithOp(ElasticsearchOperationType.Update, index, "refresh=wait_for");
        var updateProducer = updateEp.CreateProducer();
        await updateProducer.Start();

        var updateBody = new JsonObject { ["title"] = "Updated", ["views"] = 42 };
        var updateExchange = new Exchange(new Message(updateBody.ToJsonString()));
        updateExchange.In.Headers[ElasticsearchHeaders.DocumentId] = "upd-1";
        await updateProducer.Process(updateExchange);

        updateExchange.In.Headers[ElasticsearchHeaders.Result].Should().NotBeNull();
        await updateProducer.Stop();

        // Get and verify merge
        var getEp = CreateEndpointWithOp(ElasticsearchOperationType.Get, index);
        var getProducer = getEp.CreateProducer();
        await getProducer.Start();

        var getExchange = new Exchange(new Message());
        getExchange.In.Headers[ElasticsearchHeaders.DocumentId] = "upd-1";
        await getProducer.Process(getExchange);

        var result = getExchange.Out!.Body?.ToString();
        result.Should().Contain("Updated");
        result.Should().Contain("Alice", because: "author should be preserved from original");
        result.Should().Contain("42");

        await getProducer.Stop();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  DELETE
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Delete_RemovesDocument()
    {
        var index = UniqueIndex();

        // Index a document
        var ep = CreateEndpoint(index, "refresh=wait_for");
        var producer = ep.CreateProducer();
        await producer.Start();

        var body = new JsonObject { ["title"] = "To Delete" };
        var indexExchange = new Exchange(new Message(body.ToJsonString()));
        indexExchange.In.Headers[ElasticsearchHeaders.DocumentId] = "del-1";
        await producer.Process(indexExchange);
        await producer.Stop();

        // Delete the document
        var delEp = CreateEndpointWithOp(ElasticsearchOperationType.Delete, index, "refresh=wait_for");
        var delProducer = delEp.CreateProducer();
        await delProducer.Start();

        var delExchange = new Exchange(new Message());
        delExchange.In.Headers[ElasticsearchHeaders.DocumentId] = "del-1";
        await delProducer.Process(delExchange);
        await delProducer.Stop();

        // Verify via Get — should not be found
        var getEp = CreateEndpointWithOp(ElasticsearchOperationType.Get, index);
        var getProducer = getEp.CreateProducer();
        await getProducer.Start();

        var getExchange = new Exchange(new Message());
        getExchange.In.Headers[ElasticsearchHeaders.DocumentId] = "del-1";
        await getProducer.Process(getExchange);

        getExchange.Out!.Body.Should().BeNull("document was deleted");
        await getProducer.Stop();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  COUNT
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Count_ReturnsCorrectCount()
    {
        var index = UniqueIndex();

        // Index several documents
        var ep = CreateEndpoint(index, "refresh=wait_for");
        var producer = ep.CreateProducer();
        await producer.Start();

        for (int i = 0; i < 7; i++)
        {
            var doc = new JsonObject { ["seq"] = i };
            var ex = new Exchange(new Message(doc.ToJsonString()));
            ex.In.Headers[ElasticsearchHeaders.DocumentId] = $"cnt-{i}";
            await producer.Process(ex);
        }
        await producer.Stop();

        // Count
        var countEp = CreateEndpointWithOp(ElasticsearchOperationType.Count, index);
        var countProducer = countEp.CreateProducer();
        await countProducer.Start();

        var countExchange = new Exchange(new Message());
        await countProducer.Process(countExchange);

        countExchange.Pattern.Should().Be(ExchangePattern.InOut);
        ((long)countExchange.Out!.Body!).Should().Be(7);

        await countProducer.Stop();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  EXISTS
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Exists_ReturnsBoolean()
    {
        var index = UniqueIndex();

        // Index a document
        var ep = CreateEndpoint(index, "refresh=wait_for");
        var producer = ep.CreateProducer();
        await producer.Start();

        var body = new JsonObject { ["title"] = "Exists Test" };
        var indexExchange = new Exchange(new Message(body.ToJsonString()));
        indexExchange.In.Headers[ElasticsearchHeaders.DocumentId] = "ex-1";
        await producer.Process(indexExchange);
        await producer.Stop();

        // Check exists — should be true
        var existsEp = CreateEndpointWithOp(ElasticsearchOperationType.Exists, index);
        var existsProducer = existsEp.CreateProducer();
        await existsProducer.Start();

        var existsExchange = new Exchange(new Message());
        existsExchange.In.Headers[ElasticsearchHeaders.DocumentId] = "ex-1";
        await existsProducer.Process(existsExchange);

        existsExchange.Pattern.Should().Be(ExchangePattern.InOut);
        ((bool)existsExchange.Out!.Body!).Should().BeTrue();

        // Check non-existing
        var noExchange = new Exchange(new Message());
        noExchange.In.Headers[ElasticsearchHeaders.DocumentId] = "non-existing";
        await existsProducer.Process(noExchange);

        ((bool)noExchange.Out!.Body!).Should().BeFalse();

        await existsProducer.Stop();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CONSUMER — Basic Polling
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Consumer_Polls_ProcessesDocuments()
    {
        var index = UniqueIndex();
        _output.WriteLine($"Consumer index: {index}");

        // Seed documents via raw client
        for (int i = 0; i < 5; i++)
        {
            await IndexDocumentJson(index, $"poll-{i}", $"{{\"title\":\"Poll Doc {i}\",\"seq\":{i}}}");
        }
        await RefreshIndex(index);

        var ep = CreateEndpoint(index,
            "deleteAfterRead=false&delay=500&initialDelay=100&size=10&sort=seq:asc");

        var received = new ConcurrentBag<IExchange>();
        var counter = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                received.Add(ci.ArgAt<IExchange>(0));
                if (Interlocked.Increment(ref counter) >= 5) done.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(done.Task, Task.Delay(30_000));
        await consumer.Stop();

        received.Should().HaveCountGreaterThanOrEqualTo(5);

        // Verify headers set
        var first = received.First();
        first.In.Headers[ElasticsearchHeaders.IndexName].Should().Be(index);
        first.In.Headers[ElasticsearchHeaders.DocumentId].Should().NotBeNull();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CONSUMER — DeleteAfterRead
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Consumer_DeleteAfterRead_CleansIndex()
    {
        var index = UniqueIndex();
        _output.WriteLine($"DeleteAfterRead index: {index}");

        // Seed 3 documents
        for (int i = 0; i < 3; i++)
        {
            await IndexDocumentJson(index, $"dar-{i}", $"{{\"title\":\"DAR Doc {i}\"}}");
        }
        await RefreshIndex(index);

        var ep = CreateEndpoint(index,
            "deleteAfterRead=true&delay=500&initialDelay=100&size=10&refresh=wait_for");

        var counter = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (Interlocked.Increment(ref counter) >= 3) done.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(done.Task, Task.Delay(30_000));
        await consumer.Stop();

        // Index should be empty now
        await RefreshIndex(index);
        var remaining = await CountDocuments(index);
        remaining.Should().Be(0, "all documents should have been deleted after read");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  OPERATION OVERRIDE VIA HEADER
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Producer_OperationOverrideViaHeader()
    {
        var index = UniqueIndex();

        // Index a document first
        var ep = CreateEndpoint(index, "refresh=wait_for");
        var producer = ep.CreateProducer();
        await producer.Start();

        var body = new JsonObject { ["title"] = "Override Test" };
        var indexExchange = new Exchange(new Message(body.ToJsonString()));
        indexExchange.In.Headers[ElasticsearchHeaders.DocumentId] = "ovr-1";
        await producer.Process(indexExchange);

        // Use same producer (default operation=Index) but override via header to Count
        var countExchange = new Exchange(new Message());
        countExchange.In.Headers[ElasticsearchHeaders.Operation] = "Count";
        await producer.Process(countExchange);

        countExchange.Pattern.Should().Be(ExchangePattern.InOut);
        ((long)countExchange.Out!.Body!).Should().Be(1);

        await producer.Stop();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  GET — Not Found
    // ═══════════════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════════════
    //  MULTISEARCH
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task MultiSearch_MultipleQueries_ReturnsResultsPerQuery()
    {
        var index1 = UniqueIndex();
        var index2 = UniqueIndex();

        // Seed index1 with 3 docs
        var ep1 = CreateEndpoint(index1, "refresh=wait_for");
        var p1 = ep1.CreateProducer();
        await p1.Start();
        for (int i = 0; i < 3; i++)
        {
            var doc = new JsonObject { ["title"] = $"Alpha {i}", ["category"] = "alpha" };
            var ex = new Exchange(new Message(doc.ToJsonString()));
            ex.In.Headers[ElasticsearchHeaders.DocumentId] = $"a-{i}";
            await p1.Process(ex);
        }
        await p1.Stop();

        // Seed index2 with 2 docs
        var ep2 = CreateEndpoint(index2, "refresh=wait_for");
        var p2 = ep2.CreateProducer();
        await p2.Start();
        for (int i = 0; i < 2; i++)
        {
            var doc = new JsonObject { ["title"] = $"Beta {i}", ["category"] = "beta" };
            var ex = new Exchange(new Message(doc.ToJsonString()));
            ex.In.Headers[ElasticsearchHeaders.DocumentId] = $"b-{i}";
            await p2.Process(ex);
        }
        await p2.Stop();

        // MultiSearch: two queries against different indices
        var msEp = CreateEndpointWithOp(ElasticsearchOperationType.MultiSearch, index1);
        var msProducer = msEp.CreateProducer();
        await msProducer.Start();

        var queries = new JsonArray
        {
            new JsonObject { ["index"] = index1, ["query"] = new JsonObject { ["match_all"] = new JsonObject() }, ["size"] = 10 },
            new JsonObject { ["index"] = index2, ["query"] = new JsonObject { ["match_all"] = new JsonObject() }, ["size"] = 10 },
        };

        var msExchange = new Exchange(new Message(queries.ToJsonString()));
        await msProducer.Process(msExchange);

        msExchange.Pattern.Should().Be(ExchangePattern.InOut);
        msExchange.Out.Should().NotBeNull();

        var results = msExchange.Out!.Body as List<List<JsonObject>>;
        results.Should().NotBeNull();
        results.Should().HaveCount(2);
        results![0].Should().HaveCount(3, "index1 has 3 documents");
        results[1].Should().HaveCount(2, "index2 has 2 documents");

        // Verify headers
        ((int)msExchange.Out.Headers[ElasticsearchHeaders.MultiSearchResponseCount]!).Should().Be(2);
        ((bool)msExchange.Out.Headers[ElasticsearchHeaders.MultiSearchHasErrors]!).Should().BeFalse();

        var totalHits = msExchange.Out.Headers[ElasticsearchHeaders.MultiSearchTotalHits] as long[];
        totalHits.Should().NotBeNull();
        totalHits![0].Should().Be(3);
        totalHits[1].Should().Be(2);

        await msProducer.Stop();
    }

    [Fact]
    public async Task MultiSearch_WithSizeLimit_RespectsPerQuerySize()
    {
        var index = UniqueIndex();

        // Seed 5 docs
        var ep = CreateEndpoint(index, "refresh=wait_for");
        var producer = ep.CreateProducer();
        await producer.Start();
        for (int i = 0; i < 5; i++)
        {
            var doc = new JsonObject { ["seq"] = i };
            var ex = new Exchange(new Message(doc.ToJsonString()));
            ex.In.Headers[ElasticsearchHeaders.DocumentId] = $"ms-{i}";
            await producer.Process(ex);
        }
        await producer.Stop();

        // MultiSearch: one query with size=2, one without size (defaults)
        var msEp = CreateEndpointWithOp(ElasticsearchOperationType.MultiSearch, index);
        var msProducer = msEp.CreateProducer();
        await msProducer.Start();

        var queries = new JsonArray
        {
            new JsonObject { ["query"] = new JsonObject { ["match_all"] = new JsonObject() }, ["size"] = 2 },
            new JsonObject { ["query"] = new JsonObject { ["match_all"] = new JsonObject() } },
        };

        var msExchange = new Exchange(new Message(queries.ToJsonString()));
        await msProducer.Process(msExchange);

        var results = msExchange.Out!.Body as List<List<JsonObject>>;
        results.Should().NotBeNull();
        results![0].Should().HaveCount(2, "size=2 was specified");
        results[1].Count.Should().BeGreaterThanOrEqualTo(5, "no size limit, should return all 5");

        var totalHits = msExchange.Out.Headers[ElasticsearchHeaders.MultiSearchTotalHits] as long[];
        totalHits![0].Should().Be(5, "total hits should reflect all matches, not just returned docs");
        totalHits[1].Should().Be(5);

        await msProducer.Stop();
    }

    [Fact]
    public async Task MultiSearch_WithSortAndSource_AppliesPerQuery()
    {
        var index = UniqueIndex();

        // Seed 4 docs with seq field
        var ep = CreateEndpoint(index, "refresh=wait_for");
        var producer = ep.CreateProducer();
        await producer.Start();
        for (int i = 0; i < 4; i++)
        {
            var doc = new JsonObject { ["title"] = $"Doc {i}", ["seq"] = i, ["secret"] = "hidden" };
            var ex = new Exchange(new Message(doc.ToJsonString()));
            ex.In.Headers[ElasticsearchHeaders.DocumentId] = $"ss-{i}";
            await producer.Process(ex);
        }
        await producer.Stop();

        var msEp = CreateEndpointWithOp(ElasticsearchOperationType.MultiSearch, index);
        var msProducer = msEp.CreateProducer();
        await msProducer.Start();

        var queries = new JsonArray
        {
            // Query 1: sorted by seq desc, size 2 — should get docs 3,2
            new JsonObject
            {
                ["query"] = new JsonObject { ["match_all"] = new JsonObject() },
                ["sort"] = "seq:desc",
                ["size"] = 2,
            },
            // Query 2: only return title field
            new JsonObject
            {
                ["query"] = new JsonObject { ["match_all"] = new JsonObject() },
                ["_source"] = "title",
            },
        };

        var msExchange = new Exchange(new Message(queries.ToJsonString()));
        await msProducer.Process(msExchange);

        var results = msExchange.Out!.Body as List<List<JsonObject>>;
        results.Should().NotBeNull();
        results.Should().HaveCount(2);

        // Query 1: sorted desc, size 2 — first doc should have highest seq
        results![0].Should().HaveCount(2);
        var firstDocSeq = results[0][0]["seq"]!.GetValue<int>();
        var secondDocSeq = results[0][1]["seq"]!.GetValue<int>();
        firstDocSeq.Should().BeGreaterThan(secondDocSeq, "sort is seq:desc");

        // Query 2: _source filtering — docs should have "title" but NOT "secret"
        foreach (var doc in results[1])
        {
            doc.ContainsKey("title").Should().BeTrue();
            doc.ContainsKey("secret").Should().BeFalse("source filter excludes it");
        }

        await msProducer.Stop();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  GET — Not Found
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Get_NotFound_ReturnsNullBody()
    {
        var index = UniqueIndex();

        // Create the index by indexing and deleting a dummy doc
        await IndexDocumentJson(index, "dummy", "{\"x\":1}");
        await RefreshIndex(index);

        var getEp = CreateEndpointWithOp(ElasticsearchOperationType.Get, index);
        var getProducer = getEp.CreateProducer();
        await getProducer.Start();

        var getExchange = new Exchange(new Message());
        getExchange.In.Headers[ElasticsearchHeaders.DocumentId] = "nonexistent-doc";
        await getProducer.Process(getExchange);

        getExchange.Out!.Body.Should().BeNull("document does not exist");

        await getProducer.Stop();
    }
}
