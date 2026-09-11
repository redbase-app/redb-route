using System.Collections.Concurrent;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.S3;
using Xunit.Abstractions;

namespace redb.Route.Tests.S3;

/// <summary>
/// Failure semantics of the S3 polling consumer against MinIO (:9000) — Ф11 волна S-А,
/// findings S-1..S-4 of docs/V4/REVIEW-S3.md: a failed exchange must never lose the object
/// (S-1); the idempotent mark must be written after success, never at filter time — objects
/// beyond MaxMessagesPerPoll and failed objects must be re-offered (S-2); exchanges must
/// carry the endpoint's ScopeFactory so per-exchange DI scopes work (S-4).
/// </summary>
[Trait("Category", "Integration")]
public sealed class S3ConsumerFailureTests : IAsyncLifetime
{
    private const string ServiceUrl = "http://localhost:9000";
    private const string AccessKey = "minioadmin";
    private const string SecretKey = "minioadmin";
    private const string Region = "us-east-1";

    // Bucket per TFM: three test assemblies run in parallel against one MinIO.
    private static readonly string TestBucket = $"consumer-failure-net{Environment.Version.Major}";

    private readonly ITestOutputHelper _output;
    private IAmazonS3? _rawClient;

    public S3ConsumerFailureTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        _rawClient = new AmazonS3Client(AccessKey, SecretKey, new AmazonS3Config
        {
            ServiceURL = ServiceUrl,
            ForcePathStyle = true,
            AuthenticationRegion = Region,
        });
        try { await _rawClient.EnsureBucketExistsAsync(TestBucket); }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict) { }
    }

    public Task DisposeAsync()
    {
        _rawClient?.Dispose();
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  S-1 — delete/move must not happen after failed processing
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DeleteAfterRead_FailedProcessing_ObjectSurvives()
    {
        var prefix = $"fail-del-{Guid.NewGuid():N}/";
        await SeedObject(prefix + "victim.txt");

        // deleteAfterRead=true is the DEFAULT — the exact configuration every consumer runs with.
        var consumer = CreateConsumer(
            $"prefix={prefix}&delay=300&initialDelay=100&includeBody=false",
            new ThrowingProcessor());
        await consumer.Start();
        await Task.Delay(2000);
        await consumer.Stop();

        (await ObjectExists(prefix + "victim.txt")).Should().BeTrue(
            "объект с упавшей обработкой не имеет права быть удалённым");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  S-2 — idempotent mark only after success, never at filter time
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Idempotent_BurstBeyondMaxMessagesPerPoll_AllObjectsProcessed()
    {
        var prefix = $"burst-{Guid.NewGuid():N}/";
        for (var i = 0; i < 15; i++)
            await SeedObject($"{prefix}obj{i:D2}.txt");

        var processor = new CountingProcessor(expected: 15);
        var consumer = CreateConsumer(
            $"prefix={prefix}&delay=400&initialDelay=100&maxMessagesPerPoll=10" +
            "&idempotent=true&deleteAfterRead=false&includeBody=false",
            processor);
        await consumer.Start();
        var all = await Task.WhenAny(processor.AllSeen.Task, Task.Delay(15_000)) == processor.AllSeen.Task;
        await consumer.Stop();

        all.Should().BeTrue(
            "объекты сверх MaxMessagesPerPoll не имеют права помечаться «seen» на этапе фильтра");
        processor.DistinctKeys.Should().Be(15);
        _output.WriteLine($"Processed {processor.DistinctKeys} distinct keys");
    }

    [Fact]
    public async Task Idempotent_FailedObject_RetriedOnNextPoll()
    {
        var prefix = $"idem-retry-{Guid.NewGuid():N}/";
        await SeedObject(prefix + "victim.txt");

        var processor = new FailFirstProcessor();
        var consumer = CreateConsumer(
            $"prefix={prefix}&delay=300&initialDelay=100&idempotent=true" +
            "&deleteAfterRead=false&includeBody=false",
            processor);
        await consumer.Start();
        var ok = await Task.WhenAny(processor.FirstSuccess.Task, Task.Delay(8000)) == processor.FirstSuccess.Task;
        await consumer.Stop();

        ok.Should().BeTrue("упавший объект не должен быть помечен «seen» и обязан быть доставлен повторно");
        processor.TotalAttempts.Should().BeGreaterThanOrEqualTo(2);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  S-4 — exchanges carry the endpoint's ScopeFactory
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Consumer_Exchange_CarriesServiceProviderScope()
    {
        var prefix = $"scope-{Guid.NewGuid():N}/";
        await SeedObject(prefix + "one.txt");

        var endpoint = CreateEndpoint(
            $"prefix={prefix}&delay=300&initialDelay=100&deleteAfterRead=false&includeBody=false");
        await using var sp = new ServiceCollection().BuildServiceProvider();
        endpoint.ScopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var processor = new ScopeProbeProcessor();
        var consumer = endpoint.CreateConsumer(processor);
        await consumer.Start();
        var seen = await Task.WhenAny(processor.Seen.Task, Task.Delay(8000)) == processor.Seen.Task;
        await consumer.Stop();

        seen.Should().BeTrue();
        processor.HadServiceProvider.Should().BeTrue(
            "exchange консьюмера обязан создаваться с ScopeFactory эндпоинта — иначе per-exchange DI-скоупы мертвы");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Guard — success path still deletes
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DeleteAfterRead_SuccessfulProcessing_ObjectDeleted()
    {
        var prefix = $"ok-del-{Guid.NewGuid():N}/";
        await SeedObject(prefix + "victim.txt");

        var consumer = CreateConsumer(
            $"prefix={prefix}&delay=300&initialDelay=100&includeBody=false",
            new NoOpProcessor());
        await consumer.Start();
        var deleted = await WaitUntilAsync(async () => !await ObjectExists(prefix + "victim.txt"), 6000);
        await consumer.Stop();

        deleted.Should().BeTrue("успешно обработанный объект при DeleteAfterRead удаляется");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════

    private S3Endpoint CreateEndpoint(string extraParams)
    {
        var qs = $"serviceUrl={ServiceUrl}&accessKey={AccessKey}&secretKey={SecretKey}" +
                 $"&region={Region}&forcePathStyle=true&{extraParams}";
        var uri = EndpointUriParser.Parse($"s3://{TestBucket}?{qs}");
        return (S3Endpoint)new S3Component().CreateEndpoint(uri);
    }

    private IConsumer CreateConsumer(string extraParams, IProcessor processor)
        => CreateEndpoint(extraParams).CreateConsumer(processor);

    private async Task SeedObject(string key)
        => await _rawClient!.PutObjectAsync(new PutObjectRequest
        {
            BucketName = TestBucket,
            Key = key,
            ContentBody = "payload",
        });

    private async Task<bool> ObjectExists(string key)
    {
        try
        {
            await _rawClient!.GetObjectMetadataAsync(TestBucket, key);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(200);
        }
        return await condition();
    }

    private sealed class ThrowingProcessor : IProcessor
    {
        public Task Process(IExchange exchange, CancellationToken ct = default)
            => throw new InvalidOperationException("simulated processing failure");
    }

    private sealed class NoOpProcessor : IProcessor
    {
        public Task Process(IExchange exchange, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class CountingProcessor : IProcessor
    {
        private readonly int _expected;
        private readonly ConcurrentDictionary<string, bool> _keys = new();

        internal CountingProcessor(int expected) => _expected = expected;

        internal TaskCompletionSource AllSeen { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int DistinctKeys => _keys.Count;

        public Task Process(IExchange exchange, CancellationToken ct = default)
        {
            var key = exchange.In.GetHeader<string>(S3Headers.Key) ?? "?";
            _keys.TryAdd(key, true);
            if (_keys.Count >= _expected)
                AllSeen.TrySetResult();
            return Task.CompletedTask;
        }
    }

    /// <summary>Fails the first delivery of every object, succeeds afterwards.</summary>
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
            var key = exchange.In.GetHeader<string>(S3Headers.Key) ?? "?";
            var attempt = _attempts.AddOrUpdate(key, 1, (_, n) => n + 1);
            if (attempt == 1)
                throw new InvalidOperationException("simulated first-delivery failure");

            FirstSuccess.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class ScopeProbeProcessor : IProcessor
    {
        internal TaskCompletionSource Seen { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool HadServiceProvider { get; private set; }

        public Task Process(IExchange exchange, CancellationToken ct = default)
        {
            HadServiceProvider = exchange.ServiceProvider is not null;
            Seen.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
