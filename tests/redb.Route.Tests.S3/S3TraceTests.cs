using System.Diagnostics;
using Amazon.S3;
using Amazon.S3.Model;
using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.S3;
using redb.Route.Telemetry;
using redb.Route.Tests.Telemetry;

namespace redb.Route.Tests.S3;

/// <summary>
/// A polled object carries no trace context: the consumer opens a root span per routed object, never a child of the
/// activity the poll loop inherited from whoever started it, and none for an empty poll. A failed route marks it red.
/// The producer marks a failed call red. <c>EnableTelemetry=false</c> opens none of these spans. Each test polls its
/// own key prefix and reads only the spans of that endpoint. Requires MinIO at localhost:9000.
/// </summary>
[Trait("Category", "Integration")]
public sealed class S3TraceTests : IAsyncLifetime
{
    private const string ServiceUrl = "http://localhost:9000";
    private const string AccessKey = "minioadmin";
    private const string SecretKey = "minioadmin";
    private const string Region = "us-east-1";
    private static readonly string Bucket = $"trace-net{Environment.Version.Major}";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private readonly string _prefix = $"trace-{Guid.NewGuid():N}/";
    private IAmazonS3 _raw = null!;

    public async Task InitializeAsync()
    {
        _raw = new AmazonS3Client(AccessKey, SecretKey,
            new AmazonS3Config { ServiceURL = ServiceUrl, ForcePathStyle = true, AuthenticationRegion = Region });
        try { await _raw.EnsureBucketExistsAsync(Bucket); }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict) { }
    }

    public Task DisposeAsync()
    {
        _raw.Dispose();
        return Task.CompletedTask;
    }

    private string Uri(string extra) =>
        $"s3://{Bucket}?serviceUrl={ServiceUrl}&accessKey={AccessKey}&secretKey={SecretKey}&region={Region}" +
        $"&forcePathStyle=true&prefix={_prefix}&{extra}";

    private string ConsumerUri => Uri("delay=200&initialDelay=50&includeBody=false&deleteAfterRead=true");

    private RouteTelemetryProbe Spans(ActivityKind kind)
        => new(a => a.Kind == kind && RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)?.Contains(_prefix, StringComparison.Ordinal) == true);

    private Task Seed(string name) =>
        _raw.PutObjectAsync(new PutObjectRequest { BucketName = Bucket, Key = _prefix + name, ContentBody = "payload" });

    private async Task<RouteContext> StartConsumer(Action<IExchange> step, bool telemetry = true)
    {
        var ctx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = telemetry });
        ctx.AddComponent(new S3Component());
        ctx.AddRoutes(r => r.From(ConsumerUri).Process(step));
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

    [Fact]
    public async Task Each_object_opens_a_root_span_even_under_an_ambient_activity()
    {
        await Seed("a.txt");
        await Seed("b.txt");
        using var probe = Spans(ActivityKind.Consumer);

        await using (await StartConsumer(_ => { }))
            await Until(() => probe.Activities.Count >= 2);

        probe.Activities.Should().HaveCount(2, "one span per routed object");
        probe.Activities.Should().OnlyContain(a => a.ParentSpanId == default(ActivitySpanId),
            "an object carries no context, so its span starts a trace rather than joining the host's");
    }

    [Fact]
    public async Task An_empty_poll_opens_no_span()
    {
        using var probe = Spans(ActivityKind.Consumer);

        await using (await StartConsumer(_ => { }))
            await Task.Delay(1500);   // several polls of an empty prefix

        probe.Activities.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_route_marks_the_span_red()
    {
        await Seed("fail.txt");
        using var probe = Spans(ActivityKind.Consumer);

        await using (await StartConsumer(_ => throw new InvalidOperationException("route failed")))
            await Until(() => probe.Activities.Count >= 1);

        probe.Activities.Should().NotBeEmpty();
        probe.Activities.Should().OnlyContain(a => a.Status == ActivityStatusCode.Error);
    }

    [Fact]
    public async Task Tracing_off_opens_no_span()
    {
        await Seed("off.txt");
        using var probe = new RouteTelemetryProbe(a =>
            RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)?.Contains(_prefix, StringComparison.Ordinal) == true);
        var routed = new TaskCompletionSource();

        await using (await StartConsumer(_ => routed.TrySetResult(), telemetry: false))
            (await Task.WhenAny(routed.Task, Task.Delay(Wait))).Should().Be(routed.Task);

        probe.Activities.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_failed_call_marks_the_producer_span_red_and_tracing_off_opens_none(bool telemetry)
    {
        using var outer = new Activity("caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var probe = new RouteTelemetryProbe(a => a.Kind == ActivityKind.Client && a.TraceId == outer.TraceId);
        await using var ctx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = telemetry });
        ctx.AddComponent(new S3Component());
        await ctx.Start();
        using var template = new ProducerTemplate(ctx);
        template.Start();
        var exchange = new Exchange(new Message(null));
        exchange.In.Headers[S3Headers.Operation] = "GetObject";
        exchange.In.Headers[S3Headers.Key] = _prefix + "missing.txt";
        var uri = $"s3://{Bucket}?serviceUrl={ServiceUrl}&accessKey={AccessKey}&secretKey={SecretKey}&region={Region}" +
                  "&forcePathStyle=true";

        var act = () => template.SendAsync(uri, exchange);

        await act.Should().ThrowAsync<Exception>();
        if (telemetry)
            probe.Activities.Should().ContainSingle().Which.Status.Should().Be(ActivityStatusCode.Error);
        else
            probe.Activities.Should().BeEmpty();
    }
}
