using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using redb.Route.Core;
using redb.Route.Sql;
using redb.Route.Tests.Sql.Batch.Fakes;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.Batch;

/// <summary>
/// Which message bodies are batch sources, and how a streamed source is read: at most one chunk ahead of the writes, no
/// connection for an empty stream, the source's own failure ending the batch.
/// </summary>
public sealed class BatchSourceTests : IDisposable
{
    private const string Insert = "INSERT INTO source_log (val) VALUES (:#val)";

    private readonly SqliteTestHelper _db = new();

    public BatchSourceTests() => _db.Execute("CREATE TABLE source_log (val TEXT)");

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Batch_ByteArrayBody_IsNotExpandedIntoItems()
    {
        var exchange = new Exchange(new Message(new byte[] { 1, 2, 3 }));
        exchange.In.Headers["val"] = "h";

        await ProduceAsync(exchange);

        Values().Should().Equal(new object?[] { "h" }, "a byte[] body is one value, not a list of batch items");
    }

    public static TheoryData<object> ByteAndCharSequences() => new()
    {
        new ArraySegment<byte>([1, 2, 3]),
        new List<byte> { 1, 2, 3 },
        new[] { 'a', 'b', 'c' },
        new ReadOnlyMemory<byte>([1, 2, 3]),
    };

    [Theory]
    [MemberData(nameof(ByteAndCharSequences))]
    public async Task Batch_ByteOrCharSequenceBody_IsNotExpandedIntoItems(object body)
    {
        var exchange = new Exchange(new Message(body));
        exchange.In.Headers["val"] = "h";

        var thrown = await ProduceAsync(exchange);

        thrown.Should().BeNull(Outcome.Describe(thrown));
        Values().Should().Equal(new object?[] { "h" }, $"a {body.GetType().Name} is one value like a byte[], not a list of batch items");
    }

    // ── Bodies that are batch sources ───────────────────────────────────────

    [Fact]
    public async Task Source_EnumerableNotList_IsBatched()
    {
        var body = Enumerable.Range(1, 3).Select(i => new Dictionary<string, object?> { ["val"] = "v" + i });

        var thrown = await ProduceAsync(new Exchange(new Message(body)));

        thrown.Should().BeNull(Outcome.Describe(thrown));
        Values().Should().Equal(new object?[] { "v1", "v2", "v3" }, "a lazy sequence is a batch source like a list");
    }

    [Fact]
    public async Task Source_AsyncEnumerableOfReferenceType_IsBatched()
    {
        var source = new CountingAsyncSource<Dictionary<string, object?>>(
            [new() { ["val"] = "a" }, new() { ["val"] = "b" }, new() { ["val"] = "c" }]);

        var thrown = await ProduceAsync(new Exchange(new Message(source)));

        thrown.Should().BeNull(Outcome.Describe(thrown));
        Values().Should().Equal(new object?[] { "a", "b", "c" });
        source.Disposed.Should().BeTrue("the connector disposes the enumerator it opened");
    }

    [Fact]
    public async Task Source_JsonElementArray_IsBatched()
    {
        using var document = JsonDocument.Parse("""[{"val":"a"},{"val":"b"}]""");

        var thrown = await ProduceAsync(new Exchange(new Message(document.RootElement.Clone())));

        thrown.Should().BeNull(Outcome.Describe(thrown));
        Values().Should().Equal(new object?[] { "a", "b" }, "unmarshalling a JSON array to object gives a JsonElement array");
    }

    [Fact]
    public async Task Source_JsonDocumentArray_IsBatched()
    {
        using var document = JsonDocument.Parse("""[{"val":"a"},{"val":"b"}]""");

        var thrown = await ProduceAsync(new Exchange(new Message(document)));

        thrown.Should().BeNull(Outcome.Describe(thrown));
        Values().Should().Equal(new object?[] { "a", "b" });
    }

    [Fact]
    public async Task Source_JsonArray_IsBatched()
    {
        var array = JsonNode.Parse("""[{"val":"a"},{"val":"b"}]""")!.AsArray();

        var thrown = await ProduceAsync(new Exchange(new Message(array)));

        thrown.Should().BeNull(Outcome.Describe(thrown));
        Values().Should().Equal(new object?[] { "a", "b" });
    }

    // ── Bodies that stay a single statement ────────────────────────────────

    [Fact]
    public async Task Source_AsyncEnumerableOfValueType_UsesSingleStatementPath()
    {
        var source = new CountingAsyncSource<int>([1, 2, 3]);
        var exchange = new Exchange(new Message(source));
        exchange.In.Headers["val"] = "h";

        var thrown = await ProduceAsync(exchange);

        thrown.Should().BeNull(Outcome.Describe(thrown));
        Values().Should().Equal(new object?[] { "h" }, "IAsyncEnumerable<int> is not IAsyncEnumerable<object?>: covariance needs reference types");
        source.ItemsRead.Should().Be(0);
    }

    [Fact]
    public async Task Source_StringDictionaryJsonObjectXmlDocument_NotBatched()
    {
        var xml = new XmlDocument();
        xml.LoadXml("<rows><row/><row/></rows>");
        var bodies = new object[]
        {
            "text",
            new Dictionary<string, object?> { ["val"] = "dictionary" },
            JsonNode.Parse("""{"val":"json-object"}""")!.AsObject(),
            xml,
        };

        foreach (var body in bodies)
        {
            var exchange = new Exchange(new Message(body));
            exchange.In.Headers.TryAdd("val", "header");
            if (body is IDictionary<string, object?> or JsonObject)
                exchange.In.Headers.Remove("val");

            var thrown = await ProduceAsync(exchange);
            thrown.Should().BeNull($"{body.GetType().Name}: {Outcome.Describe(thrown)}");
        }

        Values().Should().Equal(new object?[] { "header", "dictionary", "json-object", "header" },
            "a string, a map and an XML document are each one value: one statement per body");
    }

    // ── Streaming ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Stream_EmptyAsyncSource_OpensNoConnection()
    {
        var connection = new FakeBatchConnection();
        var factory = new FixedConnectionFactory(connection);
        var exchange = new Exchange(new Message(new CountingAsyncSource<Dictionary<string, object?>>([])));

        var thrown = await ProduceOnFakeAsync(factory, exchange, batchSize: 10);

        thrown.Should().BeNull(Outcome.Describe(thrown));
        factory.Requests.Should().Be(0, "an empty stream writes nothing, so no connection is opened");
        exchange.In.Headers[SqlHeaders.UpdateCount].Should().Be(0);
    }

    [Fact]
    public async Task Stream_DbBatch_ReadsAtMostOneChunkAheadOfWrites()
    {
        var source = new CountingAsyncSource<Dictionary<string, object?>>(Items(10));
        var readAtEachRoundTrip = new List<int>();
        var connection = new FakeBatchConnection
        {
            SupportsBatch = true,
            OnExecuteBatch = (_, batch) =>
            {
                readAtEachRoundTrip.Add(source.ItemsRead);
                return batch.BatchCommands.Count;
            },
        };

        var thrown = await ProduceOnFakeAsync(new FixedConnectionFactory(connection), new Exchange(new Message(source)), batchSize: 3);

        thrown.Should().BeNull(Outcome.Describe(thrown));
        readAtEachRoundTrip.Should().Equal(new[] { 3, 6, 9, 10 }, "a chunk is read, then written, before the next one is read");
    }

    [Fact]
    public async Task Stream_Commands_ReadsOneItemAheadOfWrites()
    {
        var source = new CountingAsyncSource<Dictionary<string, object?>>(Items(4));
        var readAtEachStatement = new List<int>();
        var connection = new FakeBatchConnection
        {
            OnExecute = (_, _) =>
            {
                readAtEachStatement.Add(source.ItemsRead);
                return 1;
            },
        };

        var thrown = await ProduceOnFakeAsync(new FixedConnectionFactory(connection), new Exchange(new Message(source)), batchSize: 3);

        thrown.Should().BeNull(Outcome.Describe(thrown));
        readAtEachStatement.Should().Equal(1, 2, 3, 4);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stream_SourceThrows_RollsBackAndRethrowsSourceException(bool breakOnError)
    {
        var source = new CountingAsyncSource<Dictionary<string, object?>>(Items(10), failAfter: 4);
        var connection = new FakeBatchConnection { Savepoints = SavepointBehavior.Works };
        var exchange = new Exchange(new Message(source));

        var thrown = await ProduceOnFakeAsync(new FixedConnectionFactory(connection), exchange, batchSize: 3, breakOnError);

        thrown.Should().BeOfType<FakeSourceException>("the source's own failure ends the batch in either mode");
        thrown!.Data[SqlHeaders.BatchFailedIndex].Should().Be(4, "four items were read before the source failed");
        connection.Transaction!.Committed.Should().BeFalse();
        connection.Transaction.RolledBack.Should().BeTrue();
        source.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task Stream_SourceThrowsAfterCancellation_IsNotReportedAsASourceFailure()
    {
        using var cts = new CancellationTokenSource();
        // A streamed source whose driver answers the stop with its own exception (SQL Server: "Operation cancelled by user").
        var source = new CancelThenThrowSource(cts);
        var connection = new FakeBatchConnection();
        var exchange = new Exchange(new Message(source));

        var thrown = await ProduceOnFakeAsync(new FixedConnectionFactory(connection), exchange, batchSize: 3, ct: cts.Token);

        thrown.Should().BeOfType<FakeSourceException>(Outcome.Describe(thrown));
        thrown!.Data.Contains(SqlHeaders.BatchFailedIndex).Should().BeFalse("under a cancellation the writers do not report an item, and neither does the source");
        exchange.In.Headers.Should().NotContainKey(SqlHeaders.BatchFailedIndex);
        connection.Transaction!.RolledBack.Should().BeTrue();
    }

    /// <summary>Yields one item; on the next read cancels the token and throws as a driver would.</summary>
    private sealed class CancelThenThrowSource(CancellationTokenSource cts) : IAsyncEnumerable<Dictionary<string, object?>>
    {
        public IAsyncEnumerator<Dictionary<string, object?>> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new Enumerator(cts);

        private sealed class Enumerator(CancellationTokenSource cts) : IAsyncEnumerator<Dictionary<string, object?>>
        {
            private int _reads;

            public Dictionary<string, object?> Current { get; } = new() { ["val"] = "v0" };

            public ValueTask<bool> MoveNextAsync()
            {
                if (_reads++ == 0)
                    return ValueTask.FromResult(true);
                cts.Cancel();
                throw new FakeSourceException("fake: the source was interrupted by the stop");
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Stream_Cancellation_DisposesEnumerator()
    {
        using var cts = new CancellationTokenSource();
        var source = new CountingAsyncSource<Dictionary<string, object?>>(Items(10));
        var connection = new FakeBatchConnection
        {
            OnExecute = (index, _) =>
            {
                if (index == 1)
                {
                    cts.Cancel();
                    throw new OperationCanceledException(cts.Token);
                }
                return 1;
            },
        };

        var thrown = await ProduceOnFakeAsync(new FixedConnectionFactory(connection), new Exchange(new Message(source)),
            batchSize: 3, ct: cts.Token);

        thrown.Should().BeAssignableTo<OperationCanceledException>(Outcome.Describe(thrown));
        source.Disposed.Should().BeTrue("the enumerator is released when the batch is cancelled");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static List<Dictionary<string, object?>> Items(int count) =>
        Enumerable.Range(0, count).Select(i => new Dictionary<string, object?> { ["val"] = "v" + i }).ToList();

    private async Task<Exception?> ProduceAsync(Exchange exchange)
    {
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, _db.CreateFactory(), Insert, new()
        {
            ["outputType"] = "None",
            ["batchSize"] = "10",
        });
        return await Outcome.Of(() => endpoint.CreateProducer().Process(exchange, CancellationToken.None));
    }

    private static async Task<Exception?> ProduceOnFakeAsync(
        FixedConnectionFactory factory, Exchange exchange, int batchSize, bool breakOnError = true, CancellationToken ct = default)
    {
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, factory, Insert, new()
        {
            ["outputType"] = "None",
            ["batchSize"] = batchSize.ToString(),
            ["breakBatchOnError"] = breakOnError ? "true" : "false",
        });
        return await Outcome.Of(() => endpoint.CreateProducer().Process(exchange, ct));
    }

    private List<object?> Values() => _db.Query("SELECT val FROM source_log ORDER BY rowid").Select(r => r["val"]).ToList();
}
