using System.Text.Json;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Http;
using OpenTelemetry;
using OpenTelemetry.Trace;
using redb.Route.Core;
using redb.Route.Firebase;
using Xunit.Abstractions;

namespace redb.Route.Tests.Firebase.Integration;

/// <summary>
/// FCM producer e2e against the <c>fcm-echo</c> container (:18200) — Google ships no FCM
/// emulator, so the mock records HTTP v1 <c>messages:send</c> calls. The FirebaseAdmin SDK
/// is pointed at the mock through <see cref="AppOptions.HttpClientFactory"/> with a static
/// access token, so the full production send path (FirebaseMessaging → HTTP v1 envelope)
/// is exercised — only the network target differs.
/// </summary>
[Trait("Category", "Integration")]
[Collection("FirebaseEnvSensitive")]
public sealed class FcmProducerE2ETests : IAsyncLifetime
{
    private const string EchoBase = "http://localhost:18200";

    // Project id per test-class instance: three TFM assemblies hit ONE shared fcm-echo store
    // in parallel, so isolation comes from filtering by project, never from clearing the store.
    private readonly string ProjectId = $"demo-{Guid.NewGuid():N}";

    private readonly ITestOutputHelper _output;
    private readonly HttpClient _echo = new();

    public FcmProducerE2ETests(ITestOutputHelper output) => _output = output;

    private string? _prevGac;

    public Task InitializeAsync()
    {
        // Validate() needs a credential source; the mock provider supplies the real app,
        // so a dummy GOOGLE_APPLICATION_CREDENTIALS satisfies the option check honestly
        // (a fake connectionFactory name would now fail loud per Ф11 Ж-1).
        _prevGac = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
        Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", "unused-by-mock.json");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", _prevGac);
        _echo.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Fcm_TokenSend_DeliversEnvelopeToFcmV1Endpoint()
    {
        using var cred = new FcmEchoCredentialProvider(ProjectId);
        var producer = CreateProducer(cred,
            Fcm.Token("device-token-1").Title("Order update").Body("Shipped")
                .ProjectId(ProjectId).Build());

        await producer.Start();
        var exchange = new Exchange(new Message("fallback body"));
        await producer.Process(exchange);
        await producer.Stop();

        var messageId = exchange.In.GetHeader<string>(FcmHeaders.MessageId);
        messageId.Should().StartWith($"projects/{ProjectId}/messages/");
        _output.WriteLine($"MessageId: {messageId}");

        var recorded = await FetchRecordedAsync();
        recorded.Should().HaveCount(1);
        var msg = recorded[0].GetProperty("envelope").GetProperty("message");
        msg.GetProperty("token").GetString().Should().Be("device-token-1");
        msg.GetProperty("notification").GetProperty("title").GetString().Should().Be("Order update");
        msg.GetProperty("notification").GetProperty("body").GetString().Should().Be("Shipped");
    }

    [Fact]
    public async Task Fcm_TopicDataOnlyDryRun_SendsValidateOnlyWithDataPayload()
    {
        using var cred = new FcmEchoCredentialProvider(ProjectId);
        var producer = CreateProducer(cred,
            Fcm.Topic("news").DataOnly().DryRun()
                .ProjectId(ProjectId).Build());

        await producer.Start();
        var body = new Dictionary<string, string> { ["orderId"] = "42", ["status"] = "shipped" };
        var exchange = new Exchange(new Message(body));
        exchange.In.Headers[FcmHeaders.DataPrefix + "source"] = "e2e";
        await producer.Process(exchange);
        await producer.Stop();

        var recorded = await FetchRecordedAsync();
        recorded.Should().HaveCount(1);
        var envelope = recorded[0].GetProperty("envelope");
        envelope.GetProperty("validate_only").GetBoolean().Should().BeTrue("DryRun обязан уехать как validate_only");
        var msg = envelope.GetProperty("message");
        msg.GetProperty("topic").GetString().Should().Be("news");
        msg.TryGetProperty("notification", out _).Should().BeFalse("DataOnly не несёт notification");
        var data = msg.GetProperty("data");
        data.GetProperty("orderId").GetString().Should().Be("42");
        data.GetProperty("status").GetString().Should().Be("shipped");
        data.GetProperty("source").GetString().Should().Be("e2e");
    }

    [Fact]
    public async Task Fcm_ImageUrlHeader_ReachesNotification()
    {
        using var cred = new FcmEchoCredentialProvider(ProjectId);
        var producer = CreateProducer(cred,
            Fcm.Token("t1").Title("hello")
                .ProjectId(ProjectId).Build());

        await producer.Start();
        var exchange = new Exchange(new Message("body"));
        exchange.In.Headers[FcmHeaders.ImageUrl] = "https://img.example/1.png";
        await producer.Process(exchange);
        await producer.Stop();

        var recorded = await FetchRecordedAsync();
        recorded[0].GetProperty("envelope").GetProperty("message")
            .GetProperty("notification").GetProperty("image").GetString()
            .Should().Be("https://img.example/1.png",
                "заголовок FcmHeaders.ImageUrl обязан читаться, а не быть мёртвой константой");
    }

    [Fact]
    public async Task Fcm_TopicSend_SpanCarriesDestination()
    {
        using var cred = new FcmEchoCredentialProvider(ProjectId);
        var producer = CreateProducer(cred,
            Fcm.Topic("news").ProjectId(ProjectId).Build());

        var activities = new List<System.Diagnostics.Activity>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddSource(redb.Route.Telemetry.RouteActivitySource.SourceName)
            .AddInMemoryExporter(activities)
            .Build()!;

        await producer.Start();
        await producer.Process(new Exchange(new Message("body")));
        await producer.Stop();

        tracer.ForceFlush(1000);
        activities.Should().NotBeEmpty();
        activities[0].GetTagItem("messaging.destination.name").Should().Be("news",
            "FCM-спан обязан нести destination (topic/condition; для token — литерал \"token\")");
    }

    [Fact]
    public async Task Fcm_HeaderOverrides_WinOverOptions()
    {
        using var cred = new FcmEchoCredentialProvider(ProjectId);
        var producer = CreateProducer(cred,
            Fcm.Token("option-token").Title("option-title")
                .ProjectId(ProjectId).Build());

        await producer.Start();
        var exchange = new Exchange(new Message("body"));
        exchange.In.Headers[FcmHeaders.Token] = "header-token";
        exchange.In.Headers[FcmHeaders.Title] = "header-title";
        await producer.Process(exchange);
        await producer.Stop();

        var recorded = await FetchRecordedAsync();
        var msg = recorded[0].GetProperty("envelope").GetProperty("message");
        msg.GetProperty("token").GetString().Should().Be("header-token");
        msg.GetProperty("notification").GetProperty("title").GetString().Should().Be("header-title");
    }

    // ═══ Д5: multicast ═══

    [Fact]
    public async Task Fcm_Multicast_SendsToEveryTokenAndReportsCounts()
    {
        using var cred = new FcmEchoCredentialProvider(ProjectId);
        var producer = CreateProducer(cred,
            Fcm.Multicast().Title("bulk").ProjectId(ProjectId).Build());

        await producer.Start();
        var tokens = new List<string> { "mt-1", "mt-2", "mt-3" };
        var exchange = new Exchange(new Message(tokens));
        await producer.Process(exchange);
        await producer.Stop();

        exchange.In.Headers[FcmHeaders.SuccessCount].Should().Be(3,
            "multicast обязан вернуть SuccessCount — заголовок объявлен со времён первой версии");
        exchange.In.Headers[FcmHeaders.FailureCount].Should().Be(0);

        var recorded = await FetchRecordedAsync();
        recorded.Should().HaveCount(3, "по одному HTTP v1 send на токен");
        recorded.Select(m => m.GetProperty("envelope").GetProperty("message")
                .GetProperty("token").GetString())
            .Should().BeEquivalentTo(tokens);
    }

    // ═══ Д6: topic management ═══

    [Fact]
    public async Task Fcm_SubscribeToTopic_SendsIidBatchAdd()
    {
        var topic = $"news-{Guid.NewGuid():N}";
        using var cred = new FcmEchoCredentialProvider(ProjectId);
        var producer = CreateProducer(cred,
            Fcm.SubscribeToTopic(topic).ProjectId(ProjectId).Build());

        await producer.Start();
        var exchange = new Exchange(new Message(new List<string> { "sub-1", "sub-2" }));
        await producer.Process(exchange);
        await producer.Stop();

        exchange.In.Headers[FcmHeaders.SuccessCount].Should().Be(2);
        exchange.In.Headers[FcmHeaders.FailureCount].Should().Be(0);

        var ops = await FetchTopicOpsAsync(topic);
        ops.Should().HaveCount(1);
        ops[0].GetProperty("envelope").GetProperty("topicOp").GetString().Should().Be("add");
        ops[0].GetProperty("envelope").GetProperty("request")
            .GetProperty("registration_tokens").EnumerateArray()
            .Select(t => t.GetString()).Should().BeEquivalentTo(["sub-1", "sub-2"]);
    }

    [Fact]
    public async Task Fcm_UnsubscribeFromTopic_SendsIidBatchRemove()
    {
        var topic = $"news-{Guid.NewGuid():N}";
        using var cred = new FcmEchoCredentialProvider(ProjectId);
        var producer = CreateProducer(cred,
            Fcm.UnsubscribeFromTopic(topic).ProjectId(ProjectId).Build());

        await producer.Start();
        var exchange = new Exchange(new Message(new List<string> { "un-1" }));
        await producer.Process(exchange);
        await producer.Stop();

        exchange.In.Headers[FcmHeaders.SuccessCount].Should().Be(1);

        var ops = await FetchTopicOpsAsync(topic);
        ops.Should().HaveCount(1);
        ops[0].GetProperty("envelope").GetProperty("topicOp").GetString().Should().Be("remove");
    }

    // ── Helpers ──

    private async Task<List<JsonElement>> FetchTopicOpsAsync(string topic)
    {
        var json = await _echo.GetStringAsync($"{EchoBase}/messages");
        return JsonSerializer.Deserialize<List<JsonElement>>(json)!
            .Where(m => m.GetProperty("envelope").TryGetProperty("request", out var r)
                        && r.GetProperty("to").GetString() == $"/topics/{topic}")
            .ToList();
    }


    private FcmProducer CreateProducer(IFirebaseCredentialProvider cred, string uri)
    {
        var component = new FcmComponent { CredentialProvider = cred };
        var endpoint = (FcmEndpoint)component.CreateEndpoint(EndpointUriParser.Parse(uri));
        return (FcmProducer)endpoint.CreateProducer();
    }

    private async Task<List<JsonElement>> FetchRecordedAsync()
    {
        var json = await _echo.GetStringAsync($"{EchoBase}/messages");
        return JsonSerializer.Deserialize<List<JsonElement>>(json)!
            .Where(m => m.GetProperty("project").GetString() == ProjectId)
            .ToList();
    }

    /// <summary>
    /// Builds a real <see cref="FirebaseApp"/> whose HTTP traffic is redirected to fcm-echo.
    /// Static access token — no token endpoint round-trip; named app — no DEFAULT clash.
    /// </summary>
    private sealed class FcmEchoCredentialProvider : IFirebaseCredentialProvider, IDisposable
    {
        private readonly FirebaseApp _app;

        internal FcmEchoCredentialProvider(string projectId) =>
            _app = FirebaseApp.Create(new AppOptions
            {
                Credential = GoogleCredential.FromAccessToken("fake-e2e-token"),
                ProjectId = projectId,
                HttpClientFactory = new RedirectingHttpClientFactory(new Uri(EchoBase)),
            }, $"fcm-echo-{Guid.NewGuid():N}");

        public FirebaseApp GetOrCreateApp(string? credentialPath = null, string? projectId = null) => _app;

        public Google.Cloud.Firestore.FirestoreDb GetFirestoreDb(string? projectId = null, string? credentialPath = null, string? databaseId = null)
            => throw new NotSupportedException("FCM-only tests");

        public Google.Cloud.Storage.V1.StorageClient GetStorageClient(string? credentialPath = null)
            => throw new NotSupportedException("FCM-only tests");

        public void Dispose() => _app.Delete();
    }

    private sealed class RedirectingHttpClientFactory : HttpClientFactory
    {
        private readonly Uri _target;
        internal RedirectingHttpClientFactory(Uri target) => _target = target;

        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args)
            => new RedirectHandler(_target, base.CreateHandler(args));

        private sealed class RedirectHandler : DelegatingHandler
        {
            private readonly Uri _target;
            internal RedirectHandler(Uri target, HttpMessageHandler inner) : base(inner) => _target = target;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var builder = new UriBuilder(request.RequestUri!)
                {
                    Scheme = _target.Scheme,
                    Host = _target.Host,
                    Port = _target.Port,
                };
                request.RequestUri = builder.Uri;
                return base.SendAsync(request, ct);
            }
        }
    }
}
