using System.Collections.Concurrent;
using System.Text;
using Google.Cloud.Storage.V1;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Firebase;
using Xunit.Abstractions;

namespace redb.Route.Tests.Firebase.Integration;

/// <summary>
/// Failure semantics of the Storage polling consumer against fake-gcs-server
/// (docker-compose.tests.yml, container <c>firebase-gcs</c> on :4443).
/// Wave А1/А5 of docs/V4/11-FIREBASE.md: an exchange that failed processing must
/// never lose the object — no delete, no move to the success pocket, no idempotent
/// "seen" mark; with MoveFailed set the object is quarantined instead.
/// </summary>
[Trait("Category", "Integration")]
[Collection("FirebaseEnvSensitive")]
public sealed class StorageConsumerFailureTests : IAsyncLifetime
{
    private const string ProjectId = "demo-redb";
    private const string GcsEndpoint = "http://localhost:4443/storage/v1/";
    private const string GcsBaseUri = "http://localhost:4443";
    private const string TestBucket = "consumer-failure-bucket";

    private readonly ITestOutputHelper _output;
    private StorageClient? _rawGcs;
    private FirebaseCredentialProvider? _prodProvider;
    private string? _prevOldEmuVar;
    private string? _prevNewEmuVar;

    public StorageConsumerFailureTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        // STORAGE_EMULATOR_HOST satisfies Validate() AND routes the production provider
        // to fake-gcs (the SDK uses the value verbatim as base URI — full path required).
        _prevOldEmuVar = Environment.GetEnvironmentVariable("FIREBASE_STORAGE_EMULATOR_HOST");
        _prevNewEmuVar = Environment.GetEnvironmentVariable("STORAGE_EMULATOR_HOST");
        Environment.SetEnvironmentVariable("STORAGE_EMULATOR_HOST", GcsEndpoint);

        _rawGcs = new StorageClientBuilder
        {
            BaseUri = GcsEndpoint,
            UnauthenticatedAccess = true,
        }.Build();
        _prodProvider = new FirebaseCredentialProvider();

        try
        {
            await _rawGcs.CreateBucketAsync(ProjectId, TestBucket);
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // already exists
        }
    }

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("FIREBASE_STORAGE_EMULATOR_HOST", _prevOldEmuVar);
        Environment.SetEnvironmentVariable("STORAGE_EMULATOR_HOST", _prevNewEmuVar);
        _prodProvider?.Dispose();
        _rawGcs?.Dispose();
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  А1 — delete/move must not happen after failed processing
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DeleteAfterRead_FailedProcessing_ObjectSurvives()
    {
        var prefix = $"fail-del-{Guid.NewGuid():N}/";
        var name = await SeedObjectAsync(prefix, "victim.txt");

        var consumer = CreateConsumer(
            $"prefix={prefix}&delay=300&initialDelay=100&deleteAfterRead=true&includeBody=false",
            new ThrowingProcessor());
        await consumer.Start();
        await Task.Delay(2000);
        await consumer.Stop();

        var exists = await ObjectExistsAsync(name);
        exists.Should().BeTrue("объект с упавшей обработкой не имеет права быть удалённым");
    }

    [Fact]
    public async Task MoveAfterRead_FailedProcessing_ObjectNotMoved()
    {
        var prefix = $"fail-move-{Guid.NewGuid():N}/";
        var name = await SeedObjectAsync(prefix, "victim.txt");

        var consumer = CreateConsumer(
            $"prefix={prefix}&delay=300&initialDelay=100&moveAfterRead=processed/&includeBody=false",
            new ThrowingProcessor());
        await consumer.Start();
        await Task.Delay(2000);
        await consumer.Stop();

        (await ObjectExistsAsync(name)).Should().BeTrue(
            "объект с упавшей обработкой должен остаться на месте");
        (await ObjectExistsAsync("processed/" + name)).Should().BeFalse(
            "объект с упавшей обработкой не имеет права попасть в успешный карман");
    }

    [Fact]
    public async Task MoveFailed_FailedProcessing_ObjectQuarantined()
    {
        var prefix = $"quarantine-{Guid.NewGuid():N}/";
        var name = await SeedObjectAsync(prefix, "victim.txt");

        var consumer = CreateConsumer(
            $"prefix={prefix}&delay=300&initialDelay=100&moveFailed=failed/&includeBody=false",
            new ThrowingProcessor());
        await consumer.Start();
        var quarantined = await WaitUntilAsync(() => ObjectExistsAsync("failed/" + name), 6000);
        await consumer.Stop();

        quarantined.Should().BeTrue("объект с упавшей обработкой должен уехать в MoveFailed-карман");
        (await ObjectExistsAsync(name)).Should().BeFalse(
            "после карантина исходный объект должен исчезнуть из префикса поллинга");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  А5 — idempotent mark only after success
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Idempotent_FailedObject_RetriedOnNextPoll()
    {
        var prefix = $"idem-retry-{Guid.NewGuid():N}/";
        await SeedObjectAsync(prefix, "victim.txt");

        var processor = new FailFirstProcessor();
        var consumer = CreateConsumer(
            $"prefix={prefix}&delay=300&initialDelay=100&idempotent=true&includeBody=false",
            processor);
        await consumer.Start();
        var succeeded = await Task.WhenAny(processor.FirstSuccess.Task, Task.Delay(8000))
            == processor.FirstSuccess.Task;
        await consumer.Stop();

        succeeded.Should().BeTrue(
            "упавший объект не должен быть помечен «seen» и обязан быть доставлен повторно");
        processor.TotalAttempts.Should().BeGreaterThanOrEqualTo(2);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Guard — success path keeps working
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DeleteAfterRead_SuccessfulProcessing_ObjectDeleted()
    {
        var prefix = $"ok-del-{Guid.NewGuid():N}/";
        var name = await SeedObjectAsync(prefix, "victim.txt");

        var consumer = CreateConsumer(
            $"prefix={prefix}&delay=300&initialDelay=100&deleteAfterRead=true&includeBody=false",
            new NoOpProcessor());
        await consumer.Start();
        var deleted = await WaitUntilAsync(async () => !await ObjectExistsAsync(name), 6000);
        await consumer.Stop();

        deleted.Should().BeTrue("успешно обработанный объект при DeleteAfterRead удаляется");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════

    private async Task<string> SeedObjectAsync(string prefix, string fileName)
    {
        var name = prefix + fileName;
        using var data = new MemoryStream(Encoding.UTF8.GetBytes("payload"));
        await _rawGcs!.UploadObjectAsync(TestBucket, name, "text/plain", data);
        return name;
    }

    private async Task<bool> ObjectExistsAsync(string name)
    {
        try
        {
            await _rawGcs!.GetObjectAsync(TestBucket, name);
            return true;
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
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

    private IConsumer CreateConsumer(string queryParams, IProcessor processor)
    {
        // Production credential provider — the consumer connects the same way a real host does.
        var component = new FirebaseStorageComponent { CredentialProvider = _prodProvider! };
        var uri = EndpointUriParser.Parse($"fbstorage://{TestBucket}?{queryParams}");
        var endpoint = (FirebaseStorageEndpoint)component.CreateEndpoint(uri);
        return endpoint.CreateConsumer(processor);
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
            var key = exchange.In.GetHeader<string>(FirebaseStorageHeaders.ObjectName) ?? "?";
            var attempt = _attempts.AddOrUpdate(key, 1, (_, n) => n + 1);
            if (attempt == 1)
                throw new InvalidOperationException("simulated first-delivery failure");

            FirstSuccess.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
