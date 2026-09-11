using System.Collections.Concurrent;
using Google.Api.Gax;
using Google.Cloud.Firestore;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Firebase;
using Xunit.Abstractions;

namespace redb.Route.Tests.Firebase.Integration;

/// <summary>
/// Wave А4 of docs/V4/11-FIREBASE.md: a faulted Firestore listener must not kill the
/// consumer silently — the error is recorded on the endpoint and the subscription is
/// re-created. The fault is injected through the <c>SnapshotInterceptor</c> test seam
/// (a throw from the snapshot callback faults <c>ListenerTask</c> in the SDK).
/// </summary>
[Trait("Category", "Integration")]
[Collection("FirebaseEnvSensitive")]
public sealed class FirestoreListenerRecoveryTests : IAsyncLifetime
{
    private const string ProjectId = "demo-redb";
    private const string FirestoreHost = "localhost:8086";

    private readonly ITestOutputHelper _output;
    private FirestoreDb? _rawFirestore;

    public FirestoreListenerRecoveryTests(ITestOutputHelper output) => _output = output;

    public Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("FIRESTORE_EMULATOR_HOST", FirestoreHost);
        _rawFirestore = new FirestoreDbBuilder
        {
            ProjectId = ProjectId,
            EmulatorDetection = EmulatorDetection.EmulatorOnly,
        }.Build();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ListenerFault_IsRecorded_AndConsumerResubscribes()
    {
        var collection = $"recovery-{Guid.NewGuid():N}";

        // Seed a document BEFORE starting: the initial snapshot of every (re)subscription
        // delivers it, so a successful redelivery proves the listener came back.
        await _rawFirestore!.Collection(collection).Document("doc1")
            .SetAsync(new Dictionary<string, object?> { ["seed"] = true });

        // Production credential provider — the consumer connects the same way a real host does.
        using var cred = new FirebaseCredentialProvider();
        var component = new FirestoreComponent { CredentialProvider = cred };
        var uri = EndpointUriParser.Parse($"fstore://{collection}?projectId={ProjectId}");
        var endpoint = (FirestoreEndpoint)component.CreateEndpoint(uri);

        var received = new ConcurrentBag<string>();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = (FirestoreConsumer)endpoint.CreateConsumer(
            new CollectingProcessor(received, delivered));

        // Fault exactly the first snapshot: the callback throw faults ListenerTask.
        var faultArmed = 1;
        consumer.SnapshotInterceptor = _ =>
        {
            if (Interlocked.Exchange(ref faultArmed, 0) == 1)
                throw new InvalidOperationException("simulated listener fault");
        };

        await consumer.Start();
        var ok = await Task.WhenAny(delivered.Task, Task.Delay(15_000)) == delivered.Task;
        await consumer.Stop();

        ok.Should().BeTrue("после падения листенера консьюмер обязан переподписаться и доставить документ");
        received.Should().Contain("doc1");
        ((IEndpointStatistics)endpoint).Errors.Should().BeGreaterThanOrEqualTo(1,
            "падение листенера обязано быть зафиксировано в статистике эндпоинта");
        _output.WriteLine($"Recovered after fault; errors={((IEndpointStatistics)endpoint).Errors}");
    }

    private sealed class CollectingProcessor : IProcessor
    {
        private readonly ConcurrentBag<string> _received;
        private readonly TaskCompletionSource _delivered;

        internal CollectingProcessor(ConcurrentBag<string> received, TaskCompletionSource delivered)
        {
            _received = received;
            _delivered = delivered;
        }

        public Task Process(IExchange exchange, CancellationToken ct = default)
        {
            var id = exchange.In.GetHeader<string>(FirestoreHeaders.DocumentId) ?? "?";
            _received.Add(id);
            _delivered.TrySetResult();
            return Task.CompletedTask;
        }
    }

}
