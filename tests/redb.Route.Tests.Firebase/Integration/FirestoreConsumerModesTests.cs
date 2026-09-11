using System.Collections.Concurrent;
using Google.Api.Gax;
using Google.Cloud.Firestore;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Firebase;
using Xunit.Abstractions;

namespace redb.Route.Tests.Firebase.Integration;

/// <summary>
/// Consumer modes of the Firestore endpoint (Ф11 волна Г):
/// Г1 — <c>Realtime=false</c> must run an honest poll loop (Delay/InitialDelay honored),
/// not silently fall back to the snapshot listener;
/// Г5 — <c>MaxConcurrency</c> bounds parallel processing within one snapshot.
/// </summary>
[Trait("Category", "Integration")]
[Collection("FirebaseEnvSensitive")]
public sealed class FirestoreConsumerModesTests : IAsyncLifetime
{
    private const string ProjectId = "demo-redb";
    private const string FirestoreHost = "localhost:8086";

    private readonly ITestOutputHelper _output;
    private FirestoreDb? _rawFirestore;
    private FirebaseCredentialProvider? _prodProvider;

    public FirestoreConsumerModesTests(ITestOutputHelper output) => _output = output;

    public Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("FIRESTORE_EMULATOR_HOST", FirestoreHost);
        _rawFirestore = new FirestoreDbBuilder
        {
            ProjectId = ProjectId,
            EmulatorDetection = EmulatorDetection.EmulatorOnly,
        }.Build();
        _prodProvider = new FirebaseCredentialProvider();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _prodProvider?.Dispose();
        return Task.CompletedTask;
    }

    // ── Г1: honest polling ──

    [Fact]
    public async Task Polling_InitialDelay_IsHonored()
    {
        var collection = $"poll-delay-{Guid.NewGuid():N}";
        await _rawFirestore!.Collection(collection).Document("doc1")
            .SetAsync(new Dictionary<string, object?> { ["seed"] = true });

        var received = new ConcurrentBag<string>();
        var consumer = CreateConsumer(collection,
            "realtime=false&initialDelay=2500&delay=500",
            new CollectingProcessor(received));

        await consumer.Start();
        await Task.Delay(1200);
        received.Should().BeEmpty(
            "Realtime=false обязан быть честным поллингом: до InitialDelay ничего не читается");

        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (received.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(200);
        await consumer.Stop();

        received.Should().Contain("doc1", "после InitialDelay поллинг обязан доставить документ");
    }

    [Fact]
    public async Task Polling_EmitsAddedModifiedRemoved()
    {
        var collection = $"poll-amr-{Guid.NewGuid():N}";
        var changes = new ConcurrentQueue<(string DocId, string ChangeType)>();
        var consumer = CreateConsumer(collection,
            "realtime=false&initialDelay=100&delay=300",
            new ChangeCollectingProcessor(changes));

        await consumer.Start();

        var docRef = _rawFirestore!.Collection(collection).Document("doc1");
        await docRef.SetAsync(new Dictionary<string, object?> { ["v"] = 1 });
        await WaitForAsync(() => changes.Any(c => c.ChangeType == "Added"), 8000);

        await docRef.SetAsync(new Dictionary<string, object?> { ["v"] = 2 });
        await WaitForAsync(() => changes.Any(c => c.ChangeType == "Modified"), 8000);

        await docRef.DeleteAsync();
        await WaitForAsync(() => changes.Any(c => c.ChangeType == "Removed"), 8000);

        await consumer.Stop();

        changes.Select(c => c.ChangeType).Distinct()
            .Should().BeEquivalentTo(["Added", "Modified", "Removed"]);
        changes.Should().OnlyContain(c => c.DocId == "doc1");
        _output.WriteLine($"Changes: {string.Join(", ", changes.Select(c => c.ChangeType))}");
    }

    // ── Г5: bounded concurrency ──

    [Fact]
    public async Task Realtime_MaxConcurrency_BoundsParallelProcessing()
    {
        var collection = $"conc-{Guid.NewGuid():N}";
        for (var i = 0; i < 6; i++)
            await _rawFirestore!.Collection(collection).Document($"doc{i}")
                .SetAsync(new Dictionary<string, object?> { ["i"] = i });

        var processor = new ConcurrencyTrackingProcessor(holdMs: 250);
        var consumer = CreateConsumer(collection, "maxConcurrency=1", processor);

        await consumer.Start();
        await WaitForAsync(() => processor.Processed >= 6, 15000);
        await consumer.Stop();

        processor.Processed.Should().BeGreaterThanOrEqualTo(6);
        processor.MaxObserved.Should().Be(1,
            "maxConcurrency=1 обязан сериализовать обработку снапшота");
    }

    // ── Helpers ──

    private IConsumer CreateConsumer(string collection, string queryParams, IProcessor processor)
    {
        var component = new FirestoreComponent { CredentialProvider = _prodProvider! };
        var uri = EndpointUriParser.Parse($"fstore://{collection}?projectId={ProjectId}&{queryParams}");
        var endpoint = (FirestoreEndpoint)component.CreateEndpoint(uri);
        return endpoint.CreateConsumer(processor);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(150);
    }

    private sealed class CollectingProcessor : IProcessor
    {
        private readonly ConcurrentBag<string> _received;
        internal CollectingProcessor(ConcurrentBag<string> received) => _received = received;

        public Task Process(IExchange exchange, CancellationToken ct = default)
        {
            _received.Add(exchange.In.GetHeader<string>(FirestoreHeaders.DocumentId) ?? "?");
            return Task.CompletedTask;
        }
    }

    private sealed class ChangeCollectingProcessor : IProcessor
    {
        private readonly ConcurrentQueue<(string, string)> _changes;
        internal ChangeCollectingProcessor(ConcurrentQueue<(string, string)> changes) => _changes = changes;

        public Task Process(IExchange exchange, CancellationToken ct = default)
        {
            _changes.Enqueue((
                exchange.In.GetHeader<string>(FirestoreHeaders.DocumentId) ?? "?",
                exchange.In.GetHeader<string>(FirestoreHeaders.ChangeType) ?? "?"));
            return Task.CompletedTask;
        }
    }

    private sealed class ConcurrencyTrackingProcessor : IProcessor
    {
        private readonly int _holdMs;
        private int _current;
        private int _max;
        private int _processed;

        internal ConcurrencyTrackingProcessor(int holdMs) => _holdMs = holdMs;

        internal int MaxObserved => Volatile.Read(ref _max);
        internal int Processed => Volatile.Read(ref _processed);

        public async Task Process(IExchange exchange, CancellationToken ct = default)
        {
            var now = Interlocked.Increment(ref _current);
            int seen;
            while (now > (seen = Volatile.Read(ref _max)))
                Interlocked.CompareExchange(ref _max, now, seen);

            await Task.Delay(_holdMs, ct);
            Interlocked.Decrement(ref _current);
            Interlocked.Increment(ref _processed);
        }
    }
}
