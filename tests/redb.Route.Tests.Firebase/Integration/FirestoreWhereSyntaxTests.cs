using Google.Api.Gax;
using Google.Cloud.Firestore;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Firebase;
using Xunit.Abstractions;

namespace redb.Route.Tests.Firebase.Integration;

/// <summary>
/// Ф11 Г6 e2e (эмулятор): кавычки дают строгие строковые литералы, spaced
/// <c>array-contains</c> работает, <c>${...}</c> в Where резолвится per-exchange у
/// producer-Query и громко запрещён у консьюмера (запрос строится один раз на подписку).
/// </summary>
[Trait("Category", "Integration")]
[Collection("FirebaseEnvSensitive")]
public sealed class FirestoreWhereSyntaxTests : IAsyncLifetime
{
    private const string ProjectId = "demo-redb";
    private const string FirestoreHost = "localhost:8086";

    private readonly ITestOutputHelper _output;
    private FirestoreDb? _rawFirestore;
    private FirebaseCredentialProvider? _prodProvider;

    public FirestoreWhereSyntaxTests(ITestOutputHelper output) => _output = output;

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
    public async Task Query_QuotedStringLiteral_MatchesStringField()
    {
        var collection = $"where-quote-{Guid.NewGuid():N}";
        await _rawFirestore!.Collection(collection).Document("d1")
            .SetAsync(new Dictionary<string, object?> { ["status"] = "007" });

        var producer = CreateQueryProducer(collection, "status=='007'");
        await producer.Start();
        var exchange = new Exchange(new Message());
        await producer.Process(exchange);
        await producer.Stop();

        exchange.Out!.Headers[FirestoreHeaders.DocumentCount].Should().Be(1,
            "'007' в кавычках — строковый литерал и обязан матчить строковое поле");
    }

    [Fact]
    public async Task Query_ArrayContainsSpaced_MatchesArrayField()
    {
        var collection = $"where-arr-{Guid.NewGuid():N}";
        await _rawFirestore!.Collection(collection).Document("d1")
            .SetAsync(new Dictionary<string, object?> { ["tags"] = new[] { "admin", "user" } });
        await _rawFirestore.Collection(collection).Document("d2")
            .SetAsync(new Dictionary<string, object?> { ["tags"] = new[] { "guest" } });

        var producer = CreateQueryProducer(collection, "tags array-contains 'admin'");
        await producer.Start();
        var exchange = new Exchange(new Message());
        await producer.Process(exchange);
        await producer.Stop();

        exchange.Out!.Headers[FirestoreHeaders.DocumentCount].Should().Be(1);
    }

    [Fact]
    public async Task QueryProducer_WhereExpression_ResolvesPerExchange()
    {
        var collection = $"where-expr-{Guid.NewGuid():N}";
        await _rawFirestore!.Collection(collection).Document("young")
            .SetAsync(new Dictionary<string, object?> { ["age"] = 15L });
        await _rawFirestore.Collection(collection).Document("adult")
            .SetAsync(new Dictionary<string, object?> { ["age"] = 30L });

        var producer = CreateQueryProducer(collection, Uri.EscapeDataString("age>=${header.minAge}"));
        await producer.Start();

        var first = new Exchange(new Message());
        first.In.Headers["minAge"] = 18;
        await producer.Process(first);
        first.Out!.Headers[FirestoreHeaders.DocumentCount].Should().Be(1,
            "${header.minAge}=18 обязан отфильтровать младшего");

        var second = new Exchange(new Message());
        second.In.Headers["minAge"] = 10;
        await producer.Process(second);
        second.Out!.Headers[FirestoreHeaders.DocumentCount].Should().Be(2,
            "выражение обязано резолвиться заново на каждый exchange");

        await producer.Stop();
    }

    [Fact]
    public void Consumer_WhereWithExpression_FailsLoud()
    {
        var component = new FirestoreComponent { CredentialProvider = _prodProvider! };
        var uri = EndpointUriParser.Parse(
            $"fstore://any?projectId={ProjectId}&where={Uri.EscapeDataString("status==${header.x}")}");
        var endpoint = (FirestoreEndpoint)component.CreateEndpoint(uri);

        var act = () => endpoint.CreateConsumer(new NoOpProcessor());

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("producer",
                "у консьюмера запрос строится один раз на подписку — exchange для ${...} не существует");
    }

    // ═══ Д7: DatabaseId (multi-database) ═══

    [Fact]
    public async Task Producer_DatabaseId_WritesToNamedDatabase()
    {
        var collection = $"multidb-{Guid.NewGuid():N}";
        var component = new FirestoreComponent { CredentialProvider = _prodProvider! };
        var uri = EndpointUriParser.Parse(
            $"fstore://{collection}?projectId={ProjectId}&operation=Set&documentId=d1&databaseId=db-two");
        var endpoint = (FirestoreEndpoint)component.CreateEndpoint(uri);
        var producer = (FirestoreProducer)endpoint.CreateProducer();

        await producer.Start();
        await producer.Process(new Exchange(new Message(
            new Dictionary<string, object?> { ["marker"] = "named-db" })));
        await producer.Stop();

        // The named database must contain the doc...
        var namedDb = new FirestoreDbBuilder
        {
            ProjectId = ProjectId,
            DatabaseId = "db-two",
            EmulatorDetection = EmulatorDetection.EmulatorOnly,
        }.Build();
        var snapshot = await namedDb.Collection(collection).Document("d1").GetSnapshotAsync();
        snapshot.Exists.Should().BeTrue("databaseId=db-two обязан доезжать до клиента, а не игнорироваться");

        // ...and (default) must NOT.
        var defaultSnapshot = await _rawFirestore!.Collection(collection).Document("d1").GetSnapshotAsync();
        defaultSnapshot.Exists.Should().BeFalse("запись не имеет права молча падать в (default)");
    }

    // ── Helpers ──

    private FirestoreProducer CreateQueryProducer(string collection, string whereEncoded)
    {
        var component = new FirestoreComponent { CredentialProvider = _prodProvider! };
        var uri = EndpointUriParser.Parse(
            $"fstore://{collection}?projectId={ProjectId}&operation=Query&where={whereEncoded}");
        var endpoint = (FirestoreEndpoint)component.CreateEndpoint(uri);
        return (FirestoreProducer)endpoint.CreateProducer();
    }

    private sealed class NoOpProcessor : IProcessor
    {
        public Task Process(IExchange exchange, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
