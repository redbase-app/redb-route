using Google.Api.Gax;
using Google.Cloud.Firestore;
using redb.Route.Core;
using redb.Route.Firebase;
using Xunit.Abstractions;

namespace redb.Route.Tests.Firebase.Integration;

/// <summary>
/// Ф11 волна Г8: <c>DocumentIdField</c> lets BatchWrite take the document id from a field of
/// each item (the field itself is not written to the document) instead of always auto-ID.
/// </summary>
[Trait("Category", "Integration")]
[Collection("FirebaseEnvSensitive")]
public sealed class FirestoreBatchWriteTests : IAsyncLifetime
{
    private const string ProjectId = "demo-redb";
    private const string FirestoreHost = "localhost:8086";

    private readonly ITestOutputHelper _output;
    private FirestoreDb? _rawFirestore;
    private FirebaseCredentialProvider? _prodProvider;

    public FirestoreBatchWriteTests(ITestOutputHelper output) => _output = output;

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

    [Fact]
    public async Task BatchWrite_DocumentIdField_UsesFieldAndStripsIt()
    {
        var collection = $"batch-id-{Guid.NewGuid():N}";
        var producer = CreateProducer(collection, "operation=BatchWrite&documentIdField=id");

        await producer.Start();
        var items = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = "alpha", ["v"] = 1L },
            new Dictionary<string, object?> { ["id"] = "beta", ["v"] = 2L },
        };
        var exchange = new Exchange(new Message(items));
        await producer.Process(exchange);
        await producer.Stop();

        var alpha = await _rawFirestore!.Collection(collection).Document("alpha").GetSnapshotAsync();
        alpha.Exists.Should().BeTrue("id обязан браться из поля элемента, а не auto-ID");
        alpha.GetValue<long>("v").Should().Be(1L);
        alpha.ContainsField("id").Should().BeFalse("поле-источник id не пишется в данные документа");

        var beta = await _rawFirestore.Collection(collection).Document("beta").GetSnapshotAsync();
        beta.Exists.Should().BeTrue();
        _output.WriteLine("BatchWrite by DocumentIdField OK");
    }

    [Fact]
    public async Task BatchWrite_WithoutDocumentIdField_AutoIdAsBefore()
    {
        var collection = $"batch-auto-{Guid.NewGuid():N}";
        var producer = CreateProducer(collection, "operation=BatchWrite");

        await producer.Start();
        var items = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = "kept-as-data", ["v"] = 1L },
        };
        var exchange = new Exchange(new Message(items));
        await producer.Process(exchange);
        await producer.Stop();

        var snapshot = await _rawFirestore!.Collection(collection).GetSnapshotAsync();
        snapshot.Count.Should().Be(1);
        snapshot.Documents[0].Id.Should().NotBe("kept-as-data", "без опции — auto-ID");
        snapshot.Documents[0].GetValue<string>("id").Should().Be("kept-as-data",
            "без опции поле остаётся обычными данными");
    }

    private FirestoreProducer CreateProducer(string collection, string queryParams)
    {
        var component = new FirestoreComponent { CredentialProvider = _prodProvider! };
        var uri = EndpointUriParser.Parse($"fstore://{collection}?projectId={ProjectId}&{queryParams}");
        var endpoint = (FirestoreEndpoint)component.CreateEndpoint(uri);
        return (FirestoreProducer)endpoint.CreateProducer();
    }
}
