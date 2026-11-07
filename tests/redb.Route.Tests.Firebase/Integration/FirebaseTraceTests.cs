using System.Diagnostics;
using System.Text;
using Google.Api.Gax;
using Google.Cloud.Firestore;
using Google.Cloud.Storage.V1;
using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Firebase;
using redb.Route.Telemetry;
using redb.Route.Tests.Telemetry;

namespace redb.Route.Tests.Firebase.Integration;

/// <summary>
/// A Firestore document change or a Storage object carries no trace context: the consumers — Firestore in realtime and
/// in polling mode, Storage — open a root span per routed change or object, never a child of the activity the consumer
/// inherited from whoever started it, and none while nothing arrives. A failed route marks it red. The Firestore
/// producer marks a failed call red. <c>EnableTelemetry=false</c> opens none of these spans. Each test uses a collection
/// or an object prefix of its own. Expects the Firestore emulator on :8086 and fake-gcs on :4443.
/// </summary>
[Trait("Category", "Integration")]
[Collection("FirebaseEnvSensitive")]
public sealed class FirebaseTraceTests : IAsyncLifetime
{
    private const string ProjectId = "demo-redb";
    private const string GcsEndpoint = "http://localhost:4443/storage/v1/";
    private const string Bucket = "trace-bucket";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private readonly string _marker = $"trace-{Guid.NewGuid():N}";
    private FirestoreDb _firestore = null!;
    private StorageClient _gcs = null!;
    private FirebaseCredentialProvider _credentials = null!;
    private string? _previousGcsHost;

    public static TheoryData<string> FirestoreModes => new() { "realtime=true", "realtime=false&delay=300&initialDelay=50" };

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("FIRESTORE_EMULATOR_HOST", "localhost:8086");
        _previousGcsHost = Environment.GetEnvironmentVariable("STORAGE_EMULATOR_HOST");
        Environment.SetEnvironmentVariable("STORAGE_EMULATOR_HOST", GcsEndpoint);
        _firestore = new FirestoreDbBuilder { ProjectId = ProjectId, EmulatorDetection = EmulatorDetection.EmulatorOnly }.Build();
        _gcs = new StorageClientBuilder { BaseUri = GcsEndpoint, UnauthenticatedAccess = true }.Build();
        _credentials = new FirebaseCredentialProvider();
        try { await _gcs.CreateBucketAsync(ProjectId, Bucket); }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.Conflict) { }
    }

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("STORAGE_EMULATOR_HOST", _previousGcsHost);
        _credentials.Dispose();
        _gcs.Dispose();
        return Task.CompletedTask;
    }

    private RouteTelemetryProbe Spans(ActivityKind kind)
        => new(a => a.Kind == kind && RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)?.Contains(_marker, StringComparison.Ordinal) == true);

    private Task SeedDocument(string id) =>
        _firestore.Collection(_marker).Document(id).SetAsync(new Dictionary<string, object?> { ["v"] = id });

    private Task SeedObject(string name) =>
        _gcs.UploadObjectAsync(Bucket, $"{_marker}/{name}", "text/plain", new MemoryStream(Encoding.UTF8.GetBytes("payload")));

    private string FirestoreUri(string mode) => $"fstore://{_marker}?projectId={ProjectId}&{mode}";

    private string StorageUri => $"fbstorage://{Bucket}?prefix={_marker}/&delay=300&initialDelay=50&deleteAfterRead=true";

    private async Task<RouteContext> Start(string uri, Action<IExchange> step, bool telemetry = true)
    {
        var ctx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = telemetry });
        ctx.AddComponent(new FirestoreComponent { CredentialProvider = _credentials });
        ctx.AddComponent(new FirebaseStorageComponent { CredentialProvider = _credentials });
        ctx.AddRoutes(r => r.From(uri).Process(step));
        using (new Activity("host startup").SetIdFormat(ActivityIdFormat.W3C).Start())
            await ctx.Start();
        return ctx;
    }

    private static async Task Until(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!done() && DateTime.UtcNow < deadline)
            await Task.Delay(100);
    }

    private static void RootSpans(IReadOnlyList<Activity> spans, int count)
    {
        spans.Should().HaveCount(count, "one span per routed change or object");
        spans.Should().OnlyContain(a => a.ParentSpanId == default(ActivitySpanId),
            "it carries no context, so its span starts a trace rather than joining the host's");
    }

    [Theory]
    [MemberData(nameof(FirestoreModes))]
    public async Task Firestore_each_change_opens_a_root_span_even_under_an_ambient_activity(string mode)
    {
        await SeedDocument("a");
        await SeedDocument("b");
        using var probe = Spans(ActivityKind.Consumer);

        await using (await Start(FirestoreUri(mode), _ => { }))
            await Until(() => probe.Activities.Count >= 2);

        RootSpans(probe.Activities.Take(2).ToList(), 2);
    }

    [Theory]
    [MemberData(nameof(FirestoreModes))]
    public async Task Firestore_an_empty_collection_opens_no_span(string mode)
    {
        using var probe = Spans(ActivityKind.Consumer);

        await using (await Start(FirestoreUri(mode), _ => { }))
            await Task.Delay(1500);

        probe.Activities.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(FirestoreModes))]
    public async Task Firestore_a_failed_route_marks_the_span_red(string mode)
    {
        await SeedDocument("fail");
        using var probe = Spans(ActivityKind.Consumer);
        IReadOnlyList<Activity> ended;

        await using (await Start(FirestoreUri(mode), _ => throw new InvalidOperationException("route failed")))
        {
            await Until(() => probe.Activities.Count >= 1);
            ended = probe.Activities;
        }

        ended.Should().NotBeEmpty().And.OnlyContain(a => a.Status == ActivityStatusCode.Error);
    }

    [Theory]
    [MemberData(nameof(FirestoreModes))]
    public async Task Firestore_tracing_off_opens_no_span(string mode)
    {
        await SeedDocument("off");
        using var probe = new RouteTelemetryProbe(a =>
            RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)?.Contains(_marker, StringComparison.Ordinal) == true);
        var routed = new TaskCompletionSource();

        await using (await Start(FirestoreUri(mode), _ => routed.TrySetResult(), telemetry: false))
            (await Task.WhenAny(routed.Task, Task.Delay(Wait))).Should().Be(routed.Task);

        probe.Activities.Should().BeEmpty();
    }

    [Fact]
    public async Task Storage_each_object_opens_a_root_span_and_an_empty_poll_none()
    {
        using var probe = Spans(ActivityKind.Consumer);

        await using (await Start(StorageUri, _ => { }))
        {
            await Task.Delay(1000);   // polls of an empty prefix
            probe.Activities.Should().BeEmpty();
            await SeedObject("a.txt");
            await SeedObject("b.txt");
            await Until(() => probe.Activities.Count >= 2);
        }

        RootSpans(probe.Activities, 2);
    }

    [Fact]
    public async Task Storage_a_failed_route_marks_the_span_red_and_keeps_the_object()
    {
        await SeedObject("fail.txt");
        using var probe = Spans(ActivityKind.Consumer);
        IReadOnlyList<Activity> ended;

        await using (await Start(StorageUri, _ => throw new InvalidOperationException("route failed")))
        {
            await Until(() => probe.Activities.Count >= 1);
            ended = probe.Activities;
        }

        ended.Should().NotBeEmpty().And.OnlyContain(a => a.Status == ActivityStatusCode.Error);
        (await _gcs.GetObjectAsync(Bucket, $"{_marker}/fail.txt")).Should().NotBeNull("a failed object is not deleted");
    }

    [Fact]
    public async Task Storage_tracing_off_opens_no_span()
    {
        await SeedObject("off.txt");
        using var probe = new RouteTelemetryProbe(a =>
            RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)?.Contains(_marker, StringComparison.Ordinal) == true);
        var routed = new TaskCompletionSource();

        await using (await Start(StorageUri, _ => routed.TrySetResult(), telemetry: false))
            (await Task.WhenAny(routed.Task, Task.Delay(Wait))).Should().Be(routed.Task);

        probe.Activities.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Firestore_a_failed_call_marks_the_producer_span_red_and_tracing_off_opens_none(bool telemetry)
    {
        using var probe = Spans(ActivityKind.Client);
        await using var ctx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = telemetry });
        ctx.AddComponent(new FirestoreComponent { CredentialProvider = _credentials });
        await ctx.Start();
        using var template = new ProducerTemplate(ctx);
        template.Start();
        var exchange = new Exchange(new Message(new Dictionary<string, object?> { ["v"] = 1 }));
        exchange.In.Headers[FirestoreHeaders.DocumentId] = "no-such-document";

        // Update requires the document to exist.
        var act = () => template.SendAsync($"fstore://{_marker}?projectId={ProjectId}&operation=Update", exchange);

        await act.Should().ThrowAsync<Exception>();
        if (telemetry)
            probe.Activities.Should().ContainSingle().Which.Status.Should().Be(ActivityStatusCode.Error);
        else
            probe.Activities.Should().BeEmpty();
    }
}
