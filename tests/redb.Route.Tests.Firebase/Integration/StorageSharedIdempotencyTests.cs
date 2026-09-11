using System.Collections.Concurrent;
using System.Text;
using Google.Cloud.Storage.V1;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Firebase;
using redb.Route.Processors;
using Xunit.Abstractions;

namespace redb.Route.Tests.Firebase.Integration;

/// <summary>
/// Ф11 Д4: the polling consumer can use a NAMED <see cref="IIdempotentRepository"/> from the
/// context registry (the same contract the route-level IdempotentConsumer EIP uses) instead
/// of its per-consumer in-memory dictionary — deduplication survives consumer restarts and,
/// with a persistent repository (RedbIdempotentRepository), process restarts and scale-out.
/// </summary>
[Trait("Category", "Integration")]
[Collection("FirebaseEnvSensitive")]
public sealed class StorageSharedIdempotencyTests : IAsyncLifetime
{
    private const string ProjectId = "demo-redb";
    private const string GcsEndpoint = "http://localhost:4443/storage/v1/";
    private static readonly string TestBucket = $"shared-idem-net{Environment.Version.Major}";

    private readonly ITestOutputHelper _output;
    private StorageClient? _rawGcs;
    private FirebaseCredentialProvider? _prodProvider;
    private string? _prevEmuVar;

    public StorageSharedIdempotencyTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        _prevEmuVar = Environment.GetEnvironmentVariable("STORAGE_EMULATOR_HOST");
        Environment.SetEnvironmentVariable("STORAGE_EMULATOR_HOST", GcsEndpoint);
        _rawGcs = new StorageClientBuilder { BaseUri = GcsEndpoint, UnauthenticatedAccess = true }.Build();
        _prodProvider = new FirebaseCredentialProvider();
        try { await _rawGcs.CreateBucketAsync(ProjectId, TestBucket); }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.Conflict) { }
    }

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("STORAGE_EMULATOR_HOST", _prevEmuVar);
        _prodProvider?.Dispose();
        _rawGcs?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task NamedRepository_DeduplicationSurvivesConsumerRestart()
    {
        var prefix = $"restart-{Guid.NewGuid():N}/";
        using var data = new MemoryStream(Encoding.UTF8.GetBytes("once"));
        await _rawGcs!.UploadObjectAsync(TestBucket, prefix + "obj.txt", "text/plain", data);

        await using var ctx = new RouteContext();
        var component = new FirebaseStorageComponent { CredentialProvider = _prodProvider! };
        ctx.AddComponent(component);
        ctx.AddIdempotentRepository("shared", new InMemoryIdempotentRepository());

        var endpoint = (FirebaseStorageEndpoint)ctx.GetEndpoint(
            $"fbstorage://{TestBucket}?prefix={prefix}&delay=300&initialDelay=100" +
            "&idempotentRepository=shared&includeBody=false&deleteAfterRead=false");

        var received = new ConcurrentBag<string>();

        var first = endpoint.CreateConsumer(new CollectingProcessor(received));
        await first.Start();
        await WaitForAsync(() => !received.IsEmpty, 8000);
        await first.Stop();

        // A NEW consumer instance on the same endpoint — the named repo must remember the object.
        var second = endpoint.CreateConsumer(new CollectingProcessor(received));
        await second.Start();
        await Task.Delay(1200); // a couple of poll cycles
        await second.Stop();

        received.Should().HaveCount(1,
            "именованный репозиторий обязан переживать рестарт консьюмера — иначе он не отличим от in-memory");
    }

    [Fact]
    public async Task NamedRepository_FailedObject_NotMarked_Retried()
    {
        var prefix = $"retry-{Guid.NewGuid():N}/";
        using var data = new MemoryStream(Encoding.UTF8.GetBytes("retry-me"));
        await _rawGcs!.UploadObjectAsync(TestBucket, prefix + "obj.txt", "text/plain", data);

        await using var ctx = new RouteContext();
        var component = new FirebaseStorageComponent { CredentialProvider = _prodProvider! };
        ctx.AddComponent(component);
        var repo = new InMemoryIdempotentRepository();
        ctx.AddIdempotentRepository("shared", repo);

        var endpoint = (FirebaseStorageEndpoint)ctx.GetEndpoint(
            $"fbstorage://{TestBucket}?prefix={prefix}&delay=300&initialDelay=100" +
            "&idempotentRepository=shared&includeBody=false&deleteAfterRead=false");

        var processor = new FailFirstProcessor();
        var consumer = endpoint.CreateConsumer(processor);
        await consumer.Start();
        var ok = await Task.WhenAny(processor.FirstSuccess.Task, Task.Delay(8000)) == processor.FirstSuccess.Task;
        await consumer.Stop();

        ok.Should().BeTrue("упавший объект обязан сниматься из репозитория (Remove) и ретраиться");
        processor.TotalAttempts.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void UnknownRepositoryName_FailsLoud()
    {
        var component = new FirebaseStorageComponent { CredentialProvider = _prodProvider! };
        using var ctx = new RouteContextDisposable();
        ctx.Context.AddComponent(component);

        var endpoint = (FirebaseStorageEndpoint)ctx.Context.GetEndpoint(
            $"fbstorage://{TestBucket}?idempotentRepository=typo-name&delay=300");
        var consumer = endpoint.CreateConsumer(new CollectingProcessor(new ConcurrentBag<string>()));

        var act = () => consumer.Start();

        act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("typo-name")).GetAwaiter().GetResult();
    }

    // ── Helpers ──

    private sealed class RouteContextDisposable : IDisposable
    {
        public RouteContext Context { get; } = new();
        public void Dispose() => Context.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
            _received.Add(exchange.In.GetHeader<string>(FirebaseStorageHeaders.ObjectName) ?? "?");
            return Task.CompletedTask;
        }
    }

    private sealed class FailFirstProcessor : IProcessor
    {
        private readonly ConcurrentDictionary<string, int> _attempts = new();
        private int _total;

        internal TaskCompletionSource FirstSuccess { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int TotalAttempts => Volatile.Read(ref _total);

        public Task Process(IExchange exchange, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _total);
            var key = exchange.In.GetHeader<string>(FirebaseStorageHeaders.ObjectName) ?? "?";
            if (_attempts.AddOrUpdate(key, 1, (_, n) => n + 1) == 1)
                throw new InvalidOperationException("simulated first-delivery failure");
            FirstSuccess.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
