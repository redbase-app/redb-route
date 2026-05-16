using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using Google.Cloud.Storage.V1;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Firebase;
using Xunit.Abstractions;

namespace redb.Route.Tests.Firebase.Integration;

/// <summary>
/// Integration tests against Firebase emulators (docker-compose.tests.yml).
///   Firestore: localhost:8086  (FIRESTORE_EMULATOR_HOST)
///   GCS:       localhost:4443  (fake-gcs-server)
///
/// Start with:
///   docker compose -f docker-compose.tests.yml up firebase-emulators fake-gcs -d
///   dotnet test --filter "Category=Integration"
/// </summary>
[Trait("Category", "Integration")]
public sealed class FirebaseIntegrationTests : IAsyncLifetime
{
    private const string ProjectId = "demo-redb";
    private const string FirestoreHost = "localhost:8086";
    private const string GcsEndpoint = "http://localhost:4443/storage/v1/";
    private const string GcsBaseUri = "http://localhost:4443";
    private const string TestBucket = "integration-test-bucket";

    private readonly ITestOutputHelper _output;
    private StorageClient? _rawGcs;
    private FirestoreDb? _rawFirestore;

    public FirebaseIntegrationTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        // Firestore emulator is auto-detected via env var
        Environment.SetEnvironmentVariable("FIRESTORE_EMULATOR_HOST", FirestoreHost);
        _rawFirestore = new FirestoreDbBuilder
        {
            ProjectId = ProjectId,
            EmulatorDetection = Google.Api.Gax.EmulatorDetection.EmulatorOnly,
        }.Build();

        // Create GCS client pointing at fake-gcs-server
        _rawGcs = CreateFakeGcsClient();

        // Create test bucket (ignore if exists)
        try
        {
            await _rawGcs.CreateBucketAsync(ProjectId, TestBucket);
            _output.WriteLine($"Created bucket: {TestBucket}");
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.Conflict)
        {
            _output.WriteLine($"Bucket already exists: {TestBucket}");
        }
    }

    public async Task DisposeAsync()
    {
        // Cleanup GCS objects
        if (_rawGcs is not null)
        {
            try
            {
                var objects = _rawGcs.ListObjects(TestBucket, "test-");
                foreach (var obj in objects)
                    await _rawGcs.DeleteObjectAsync(TestBucket, obj.Name);
            }
            catch { /* best-effort cleanup */ }
        }
    }

    // ── Firestore Component Helpers ──

    private FirestoreComponent CreateFirestoreComponent()
    {
        var cred = new EmulatorCredentialProvider(_rawFirestore!, _rawGcs!);
        var component = new FirestoreComponent { CredentialProvider = cred };
        return component;
    }

    private FirestoreEndpoint CreateFirestoreEndpoint(string collection, string? extraParams = null)
    {
        var qs = $"projectId={ProjectId}";
        if (extraParams is not null) qs += $"&{extraParams}";
        var uri = EndpointUriParser.Parse($"fstore://{collection}?{qs}");
        var component = CreateFirestoreComponent();
        return (FirestoreEndpoint)component.CreateEndpoint(uri);
    }

    // ── Storage Component Helpers ──

    private FirebaseStorageComponent CreateStorageComponent()
    {
        var cred = new EmulatorCredentialProvider(_rawFirestore!, _rawGcs!);
        var component = new FirebaseStorageComponent { CredentialProvider = cred };
        return component;
    }

    private FirebaseStorageEndpoint CreateStorageEndpoint(string bucket, string? extraParams = null)
    {
        // Set env var so Validate() passes
        Environment.SetEnvironmentVariable("FIREBASE_STORAGE_EMULATOR_HOST", GcsBaseUri);
        var qs = extraParams ?? "";
        var uri = EndpointUriParser.Parse(string.IsNullOrEmpty(qs)
            ? $"fbstorage://{bucket}"
            : $"fbstorage://{bucket}?{qs}");
        var component = CreateStorageComponent();
        return (FirebaseStorageEndpoint)component.CreateEndpoint(uri);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  FIRESTORE — CRUD
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Firestore_SetAndGet_Roundtrip()
    {
        var collection = $"test-{Guid.NewGuid():N}";
        var docId = "doc1";

        // Set
        var epSet = CreateFirestoreEndpoint(collection, $"operation=Set&documentId={docId}");
        var producer = (FirestoreProducer)epSet.CreateProducer();
        await producer.Start();

        var data = new Dictionary<string, object?> { ["name"] = "Alice", ["age"] = 30 };
        var setExchange = new Exchange(new Message(data));
        await producer.Process(setExchange);
        await producer.Stop();

        setExchange.In.Headers[FirestoreHeaders.DocumentId].Should().Be(docId);
        _output.WriteLine($"Set doc: {setExchange.In.Headers[FirestoreHeaders.DocumentPath]}");

        // Get
        var epGet = CreateFirestoreEndpoint(collection, $"operation=Get&documentId={docId}");
        var getProd = (FirestoreProducer)epGet.CreateProducer();
        await getProd.Start();

        var getExchange = new Exchange(new Message());
        await getProd.Process(getExchange);
        await getProd.Stop();

        getExchange.Out.Should().NotBeNull();
        var result = getExchange.Out!.Body as IDictionary<string, object?>;
        result.Should().NotBeNull();
        result!["name"].Should().Be("Alice");
        ((long)result["age"]!).Should().Be(30);
        _output.WriteLine($"Got doc: name={result["name"]}, age={result["age"]}");
    }

    [Fact]
    public async Task Firestore_SetFromJson_Roundtrip()
    {
        var collection = $"test-{Guid.NewGuid():N}";
        var docId = "json-doc";

        // Set from JSON string (exercises CRITICAL 3 fix — JsonElement conversion)
        var epSet = CreateFirestoreEndpoint(collection, $"operation=Set&documentId={docId}");
        var producer = (FirestoreProducer)epSet.CreateProducer();
        await producer.Start();

        var json = JsonSerializer.Serialize(new
        {
            name = "Bob",
            tags = new[] { "admin", "user" },
            address = new { city = "Moscow", zip = "101000" }
        });
        var setExchange = new Exchange(new Message(json));
        await producer.Process(setExchange);
        await producer.Stop();

        // Get back
        var epGet = CreateFirestoreEndpoint(collection, $"operation=Get&documentId={docId}");
        var getProd = (FirestoreProducer)epGet.CreateProducer();
        await getProd.Start();

        var getExchange = new Exchange(new Message());
        await getProd.Process(getExchange);
        await getProd.Stop();

        var result = getExchange.Out!.Body as IDictionary<string, object?>;
        result.Should().NotBeNull();
        result!["name"].Should().Be("Bob");
        (result["tags"] as IEnumerable<object>)!.Should().Contain("admin").And.Contain("user");

        var addr = result["address"] as IDictionary<string, object?>;
        addr.Should().NotBeNull();
        addr!["city"].Should().Be("Moscow");
        _output.WriteLine("JSON roundtrip OK with nested objects");
    }

    [Fact]
    public async Task Firestore_Update_ModifiesDocument()
    {
        var collection = $"test-{Guid.NewGuid():N}";
        var docId = "upd1";

        // Create
        var epSet = CreateFirestoreEndpoint(collection, $"operation=Set&documentId={docId}");
        var setProd = (FirestoreProducer)epSet.CreateProducer();
        await setProd.Start();
        await setProd.Process(new Exchange(new Message(
            new Dictionary<string, object?> { ["status"] = "draft", ["version"] = 1 })));
        await setProd.Stop();

        // Update
        var epUpd = CreateFirestoreEndpoint(collection, $"operation=Update&documentId={docId}");
        var updProd = (FirestoreProducer)epUpd.CreateProducer();
        await updProd.Start();
        await updProd.Process(new Exchange(new Message(
            new Dictionary<string, object?> { ["status"] = "published" })));
        await updProd.Stop();

        // Verify
        var snap = await _rawFirestore!.Collection(collection).Document(docId).GetSnapshotAsync();
        snap.GetValue<string>("status").Should().Be("published");
        snap.GetValue<long>("version").Should().Be(1); // untouched
    }

    [Fact]
    public async Task Firestore_Delete_RemovesDocument()
    {
        var collection = $"test-{Guid.NewGuid():N}";
        var docId = "del1";

        // Create
        await _rawFirestore!.Collection(collection).Document(docId)
            .SetAsync(new Dictionary<string, object> { ["temp"] = true });

        // Delete via producer
        var ep = CreateFirestoreEndpoint(collection, $"operation=Delete&documentId={docId}");
        var prod = (FirestoreProducer)ep.CreateProducer();
        await prod.Start();
        await prod.Process(new Exchange(new Message()));
        await prod.Stop();

        var snap = await _rawFirestore.Collection(collection).Document(docId).GetSnapshotAsync();
        snap.Exists.Should().BeFalse();
    }

    [Fact]
    public async Task Firestore_Query_ReturnsFilteredDocs()
    {
        var collection = $"test-{Guid.NewGuid():N}";

        // Seed
        for (int i = 1; i <= 5; i++)
        {
            await _rawFirestore!.Collection(collection).Document($"d{i}")
                .SetAsync(new Dictionary<string, object> { ["score"] = i * 10, ["name"] = $"user{i}" });
        }

        // Query: score >= 30
        var ep = CreateFirestoreEndpoint(collection, "operation=Query&where=score>=30&orderBy=score&limit=10");
        var prod = (FirestoreProducer)ep.CreateProducer();
        await prod.Start();

        var exchange = new Exchange(new Message());
        await prod.Process(exchange);
        await prod.Stop();

        var docs = exchange.Out!.Body as List<Dictionary<string, object>>;
        docs.Should().NotBeNull();
        docs!.Count.Should().Be(3);
        _output.WriteLine($"Query returned {docs.Count} docs (expected 3)");
    }

    [Fact]
    public async Task Firestore_BatchWrite_ChunksOver500()
    {
        var collection = $"test-{Guid.NewGuid():N}";

        // Create 510 items to exercise the 500-limit chunking
        var items = Enumerable.Range(1, 510)
            .Select(i => (IDictionary<string, object?>)new Dictionary<string, object?> { ["idx"] = i })
            .ToList();

        var ep = CreateFirestoreEndpoint(collection, "operation=BatchWrite");
        var prod = (FirestoreProducer)ep.CreateProducer();
        await prod.Start();

        var exchange = new Exchange(new Message(items));
        await prod.Process(exchange);
        await prod.Stop();

        exchange.In.Headers[FirestoreHeaders.DocumentCount].Should().Be(510);
        _output.WriteLine("BatchWrite 510 docs OK (chunked at 500)");

        // Spot-check
        var snap = await _rawFirestore!.Collection(collection).Limit(600).GetSnapshotAsync();
        snap.Count.Should().Be(510);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  FIRESTORE — Consumer (realtime listener)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Firestore_Consumer_ReceivesChanges()
    {
        var collection = $"test-{Guid.NewGuid():N}";
        var received = new ConcurrentBag<IExchange>();
        var tcs = new TaskCompletionSource();

        var ep = CreateFirestoreEndpoint(collection, $"projectId={ProjectId}");
        var processor = new CollectorProcessor(received, expectedCount: 2, tcs);
        var consumer = ep.CreateConsumer(processor);
        await consumer.Start();

        // Insert 2 docs — should trigger snapshot callback
        await _rawFirestore!.Collection(collection).Document("c1")
            .SetAsync(new Dictionary<string, object> { ["msg"] = "hello" });
        await _rawFirestore!.Collection(collection).Document("c2")
            .SetAsync(new Dictionary<string, object> { ["msg"] = "world" });

        // Wait up to 10s for both events
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(10_000));
        await consumer.Stop();

        received.Count.Should().BeGreaterThanOrEqualTo(2);
        _output.WriteLine($"Consumer received {received.Count} changes");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  STORAGE — Upload / Download / Delete / List
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Storage_UploadAndDownload_Roundtrip()
    {
        var objectName = $"test-{Guid.NewGuid():N}/hello.txt";

        // Upload
        var epUp = CreateStorageEndpoint(TestBucket, $"operation=Upload&objectName={objectName}");
        var uploadProd = (FirebaseStorageProducer)epUp.CreateProducer();
        await uploadProd.Start();

        var upExchange = new Exchange(new Message("Hello Firebase Storage!"));
        await uploadProd.Process(upExchange);
        await uploadProd.Stop();

        upExchange.In.Headers[FirebaseStorageHeaders.ObjectName].Should().Be(objectName);
        _output.WriteLine($"Uploaded: {objectName}");

        // Download
        var epDown = CreateStorageEndpoint(TestBucket, $"operation=Download&objectName={objectName}");
        var downloadProd = (FirebaseStorageProducer)epDown.CreateProducer();
        await downloadProd.Start();

        var downExchange = new Exchange(new Message());
        await downloadProd.Process(downExchange);
        await downloadProd.Stop();

        var body = downExchange.Out!.Body as byte[];
        body.Should().NotBeNull();
        Encoding.UTF8.GetString(body!).Should().Be("Hello Firebase Storage!");
        _output.WriteLine("Download roundtrip OK");
    }

    [Fact]
    public async Task Storage_Upload_BinaryBody()
    {
        var objectName = $"test-{Guid.NewGuid():N}/binary.bin";

        var epUp = CreateStorageEndpoint(TestBucket, $"operation=Upload&objectName={objectName}");
        var producer = (FirebaseStorageProducer)epUp.CreateProducer();
        await producer.Start();

        var data = new byte[] { 0x00, 0x01, 0xFF, 0xFE, 0x42, 0x43 };
        var exchange = new Exchange(new Message(data));
        await producer.Process(exchange);
        await producer.Stop();

        // Verify via raw client
        using var ms = new MemoryStream();
        await _rawGcs!.DownloadObjectAsync(TestBucket, objectName, ms);
        ms.ToArray().Should().BeEquivalentTo(data);
    }

    [Fact]
    public async Task Storage_Delete_RemovesObject()
    {
        var objectName = $"test-{Guid.NewGuid():N}/to-delete.txt";

        // Seed via raw client
        using var data = new MemoryStream(Encoding.UTF8.GetBytes("delete me"));
        await _rawGcs!.UploadObjectAsync(TestBucket, objectName, "text/plain", data);

        // Delete via producer
        var ep = CreateStorageEndpoint(TestBucket, $"operation=Delete&objectName={objectName}");
        var producer = (FirebaseStorageProducer)ep.CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message()));
        await producer.Stop();

        // Verify gone
        var exists = false;
        try { await _rawGcs.GetObjectAsync(TestBucket, objectName); exists = true; }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound) { }
        exists.Should().BeFalse();
    }

    [Fact]
    public async Task Storage_List_ReturnsObjects()
    {
        var prefix = $"test-{Guid.NewGuid():N}/";

        // Seed 3 objects
        for (int i = 1; i <= 3; i++)
        {
            using var data = new MemoryStream(Encoding.UTF8.GetBytes($"content-{i}"));
            await _rawGcs!.UploadObjectAsync(TestBucket, $"{prefix}file{i}.txt", "text/plain", data);
        }

        var ep = CreateStorageEndpoint(TestBucket, $"operation=List&prefix={prefix}");
        var producer = (FirebaseStorageProducer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message());
        await producer.Process(exchange);
        await producer.Stop();

        var list = exchange.Out!.Body as List<object>;
        list.Should().NotBeNull();
        list!.Count.Should().Be(3);
        _output.WriteLine($"Listed {list.Count} objects");
    }

    [Fact]
    public async Task Storage_GetMetadata_ReturnsInfo()
    {
        var objectName = $"test-{Guid.NewGuid():N}/meta.txt";

        using var data = new MemoryStream(Encoding.UTF8.GetBytes("metadata test"));
        await _rawGcs!.UploadObjectAsync(TestBucket, objectName, "text/plain", data);

        var ep = CreateStorageEndpoint(TestBucket, $"operation=GetMetadata&objectName={objectName}");
        var producer = (FirebaseStorageProducer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message());
        await producer.Process(exchange);
        await producer.Stop();

        exchange.Out!.Headers[FirebaseStorageHeaders.ContentType].Should().Be("text/plain");
        exchange.Out.Headers[FirebaseStorageHeaders.ObjectName].Should().Be(objectName);
        _output.WriteLine($"Metadata OK: {exchange.Out.Headers[FirebaseStorageHeaders.ContentType]}");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  STORAGE — Consumer (polling)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Storage_Consumer_PollsNewObjects()
    {
        var prefix = $"test-{Guid.NewGuid():N}/";
        var received = new ConcurrentBag<IExchange>();
        var tcs = new TaskCompletionSource();

        var ep = CreateStorageEndpoint(TestBucket,
            $"prefix={prefix}&delay=500&initialDelay=200&includeBody=true&maxMessagesPerPoll=10");
        var processor = new CollectorProcessor(received, expectedCount: 2, tcs);
        var consumer = ep.CreateConsumer(processor);
        await consumer.Start();

        // Seed 2 objects
        for (int i = 1; i <= 2; i++)
        {
            using var data = new MemoryStream(Encoding.UTF8.GetBytes($"poll-body-{i}"));
            await _rawGcs!.UploadObjectAsync(TestBucket, $"{prefix}poll{i}.txt", "text/plain", data);
        }

        // Wait for poll cycle
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();

        received.Count.Should().BeGreaterThanOrEqualTo(2);
        _output.WriteLine($"Storage consumer polled {received.Count} objects");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static StorageClient CreateFakeGcsClient()
    {
        // fake-gcs-server accepts unauthenticated requests.
        // StorageClientBuilder lets us override the endpoint + skip auth.
        return new StorageClientBuilder
        {
            BaseUri = GcsEndpoint,
            UnauthenticatedAccess = true,
        }.Build();
    }

    /// <summary>
    /// Credential provider wired to emulators — returns pre-created clients.
    /// </summary>
    private sealed class EmulatorCredentialProvider : IFirebaseCredentialProvider
    {
        private readonly FirestoreDb _firestoreDb;
        private readonly StorageClient _storageClient;

        internal EmulatorCredentialProvider(FirestoreDb firestoreDb, StorageClient storageClient)
        {
            _firestoreDb = firestoreDb;
            _storageClient = storageClient;
        }

        public FirebaseAdmin.FirebaseApp GetOrCreateApp(string? credentialPath = null, string? projectId = null)
            => throw new NotSupportedException("Emulator tests don't use FirebaseApp");

        public FirestoreDb GetFirestoreDb(string? projectId = null) => _firestoreDb;
        public StorageClient GetStorageClient() => _storageClient;
    }

    /// <summary>
    /// Collects exchanges and signals when expected count is reached.
    /// </summary>
    private sealed class CollectorProcessor : IProcessor
    {
        private readonly ConcurrentBag<IExchange> _bag;
        private readonly int _expectedCount;
        private readonly TaskCompletionSource _tcs;

        internal CollectorProcessor(ConcurrentBag<IExchange> bag, int expectedCount, TaskCompletionSource tcs)
        {
            _bag = bag;
            _expectedCount = expectedCount;
            _tcs = tcs;
        }

        public Task Process(IExchange exchange, CancellationToken ct = default)
        {
            _bag.Add(exchange);
            if (_bag.Count >= _expectedCount)
                _tcs.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
