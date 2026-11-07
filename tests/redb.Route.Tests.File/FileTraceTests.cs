using System.Diagnostics;
using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.File;
using redb.Route.Telemetry;
using redb.Route.Tests.Telemetry;

namespace redb.Route.Tests.File;

/// <summary>
/// A polled file carries no trace context: the consumer opens a root span per routed file, never a child of the activity
/// the poll loop inherited from whoever started it, and none for an empty poll. A failed route marks it red. The
/// producer marks a failed write red. <c>EnableTelemetry=false</c> opens none of these spans. Each test polls a directory
/// of its own and reads only the spans of that endpoint.
/// </summary>
public sealed class FileTraceTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly string _root;
    private readonly string _marker;

    public FileTraceTests()
    {
        _marker = "redb-trace-" + Guid.NewGuid().ToString("N")[..12];
        _root = Path.Combine(Path.GetTempPath(), _marker);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Uri(string directory, string options) =>
        "file:///" + Path.Combine(_root, directory).Replace("\\", "/").TrimStart('/') + "?" + options;

    private string ConsumerUri => Uri("in", "delay=100&initialDelay=10&delete=true");

    private RouteTelemetryProbe Spans(ActivityKind kind)
        => new(a => a.Kind == kind && RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)?.Contains(_marker, StringComparison.Ordinal) == true);

    private void Write(string name)
    {
        Directory.CreateDirectory(Path.Combine(_root, "in"));
        System.IO.File.WriteAllText(Path.Combine(_root, "in", name), "payload");
    }

    private async Task<RouteContext> StartConsumer(Action<IExchange> step, bool telemetry = true)
    {
        Directory.CreateDirectory(Path.Combine(_root, "in"));
        var ctx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = telemetry });
        ctx.AddComponent(new FileComponent());
        ctx.AddRoutes(r => r.From(ConsumerUri).Process(step));
        using (new Activity("host startup").SetIdFormat(ActivityIdFormat.W3C).Start())
            await ctx.Start();
        return ctx;
    }

    private static async Task Until(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!done() && DateTime.UtcNow < deadline)
            await Task.Delay(50);
    }

    [Fact]
    public async Task Each_file_opens_a_root_span_even_under_an_ambient_activity()
    {
        Write("a.txt");
        Write("b.txt");
        using var probe = Spans(ActivityKind.Consumer);

        await using (await StartConsumer(_ => { }))
            await Until(() => probe.Activities.Count >= 2);

        probe.Activities.Should().HaveCount(2, "one span per routed file");
        probe.Activities.Should().OnlyContain(a => a.ParentSpanId == default(ActivitySpanId),
            "a file carries no context, so its span starts a trace rather than joining the host's");
    }

    [Fact]
    public async Task An_empty_poll_opens_no_span()
    {
        using var probe = Spans(ActivityKind.Consumer);

        await using (await StartConsumer(_ => { }))
            await Task.Delay(800);   // several polls of an empty directory

        probe.Activities.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_route_marks_the_span_red()
    {
        Write("fail.txt");
        using var probe = Spans(ActivityKind.Consumer);

        await using (await StartConsumer(_ => throw new InvalidOperationException("route failed")))
            await Until(() => probe.Activities.Count >= 1);

        probe.Activities.Should().NotBeEmpty();
        probe.Activities.Should().OnlyContain(a => a.Status == ActivityStatusCode.Error);
    }

    [Fact]
    public async Task Tracing_off_opens_no_span()
    {
        Write("off.txt");
        using var probe = new RouteTelemetryProbe(a =>
            RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)?.Contains(_marker, StringComparison.Ordinal) == true);
        var routed = new TaskCompletionSource();

        await using (await StartConsumer(_ => routed.TrySetResult(), telemetry: false))
            (await Task.WhenAny(routed.Task, Task.Delay(Wait))).Should().Be(routed.Task);

        probe.Activities.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_failed_write_marks_the_producer_span_red_and_tracing_off_opens_none(bool telemetry)
    {
        Directory.CreateDirectory(Path.Combine(_root, "out"));
        System.IO.File.WriteAllText(Path.Combine(_root, "out", "taken.txt"), "already here");
        using var probe = Spans(ActivityKind.Producer);
        await using var ctx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = telemetry });
        ctx.AddComponent(new FileComponent());
        await ctx.Start();
        using var template = new ProducerTemplate(ctx);
        template.Start();

        var act = () => template.SendAsync(Uri("out", "fileName=taken.txt&fileExist=Fail"), new Exchange(new Message("payload")));

        await act.Should().ThrowAsync<IOException>();
        if (telemetry)
            probe.Activities.Should().ContainSingle().Which.Status.Should().Be(ActivityStatusCode.Error);
        else
            probe.Activities.Should().BeEmpty();
    }
}
