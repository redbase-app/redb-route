using System.Diagnostics;
using System.Text;
using FluentFTP;
using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Ftp;
using redb.Route.Telemetry;
using redb.Route.Tests.Telemetry;

namespace redb.Route.Tests.Ftp;

/// <summary>
/// A polled remote file carries no trace context: the consumer opens a root span per routed file, never a child of the
/// activity the poll loop inherited from whoever started it, and none for an empty poll. A failed route marks it red.
/// <c>EnableTelemetry=false</c> opens none. Each test polls a directory of its own and reads only the spans of that
/// endpoint. Expects FTP at localhost:21 (testuser/secret).
/// </summary>
[Trait("Category", "Integration")]
public sealed class FtpTraceTests : IAsyncLifetime
{
    private const string Host = "localhost";
    private const int Port = 21;
    private const string Username = "testuser";
    private const string Password = "secret";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private readonly string _dir = $"trace-{Guid.NewGuid():N}";
    private AsyncFtpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = new AsyncFtpClient(Host, Username, Password, Port);
        await _client.Connect();
        await _client.CreateDirectory($"/{_dir}");
    }

    public async Task DisposeAsync()
    {
        if (await _client.DirectoryExists($"/{_dir}"))
        {
            foreach (var item in await _client.GetListing($"/{_dir}"))
                if (item.Type == FtpObjectType.File)
                    await _client.DeleteFile(item.FullName);
            await _client.DeleteDirectory($"/{_dir}");
        }
        _client.Dispose();
    }

    private string Uri => $"ftp:///{_dir}?host={Host}&port={Port}&username={Username}&password={Password}" +
                          "&delay=200&initialDelay=10&delete=true";

    private RouteTelemetryProbe Spans()
        => new(a => RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)?.Contains(_dir, StringComparison.Ordinal) == true);

    private async Task Seed(string name)
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("payload"));
        await _client.UploadStream(content, $"/{_dir}/{name}", FtpRemoteExists.Overwrite, true);
    }

    private async Task<RouteContext> StartConsumer(Action<IExchange> step, bool telemetry = true)
    {
        var ctx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = telemetry });
        ctx.AddComponent(new FtpComponent());
        ctx.AddRoutes(r => r.From(Uri).Process(step));
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
    public async Task Each_file_opens_a_root_span_even_under_an_ambient_activity()
    {
        await Seed("a.txt");
        await Seed("b.txt");
        using var probe = Spans();

        await using (await StartConsumer(_ => { }))
            await Until(() => probe.Activities.Count(a => a.Kind == ActivityKind.Consumer) >= 2);

        var receives = probe.Activities.Where(a => a.Kind == ActivityKind.Consumer).ToList();
        receives.Should().HaveCount(2, "one span per routed file");
        receives.Should().OnlyContain(a => a.ParentSpanId == default(ActivitySpanId),
            "a file carries no context, so its span starts a trace rather than joining the host's");
        receives[0].GetTagItem("redb.system").Should().Be("ftp");
    }

    [Fact]
    public async Task An_empty_poll_opens_no_span()
    {
        using var probe = Spans();

        await using (await StartConsumer(_ => { }))
            await Task.Delay(1500);   // several polls of an empty directory

        probe.Activities.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_route_marks_the_span_red()
    {
        await Seed("fail.txt");
        using var probe = Spans();

        await using (await StartConsumer(_ => throw new InvalidOperationException("route failed")))
            await Until(() => probe.Activities.Any(a => a.Kind == ActivityKind.Consumer));

        probe.Activities.Where(a => a.Kind == ActivityKind.Consumer).Should().NotBeEmpty()
            .And.OnlyContain(a => a.Status == ActivityStatusCode.Error);
    }

    [Fact]
    public async Task Tracing_off_opens_no_span()
    {
        await Seed("off.txt");
        using var probe = Spans();
        var routed = new TaskCompletionSource();

        await using (await StartConsumer(_ => routed.TrySetResult(), telemetry: false))
            (await Task.WhenAny(routed.Task, Task.Delay(Wait))).Should().Be(routed.Task);

        probe.Activities.Should().BeEmpty();
    }
}
