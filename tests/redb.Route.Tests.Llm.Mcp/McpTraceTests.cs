using System.Diagnostics;
using redb.Route.Configuration;
using redb.Route.Tests.Telemetry;

namespace redb.Route.Tests.Llm.Mcp;

/// <summary>
/// The <c>tools/call</c> span is a transport span of its endpoint: it carries <c>redb.route.endpoint</c>, honours
/// <c>EnableTelemetry</c>, and a failed call — an unknown or dead server, a tool error thrown by the client, a timeout —
/// marks it red; our own cancellation does not. Stub clients only: no MCP server is started.
/// </summary>
public sealed class McpTraceTests
{
    private readonly string _server = $"trace{Guid.NewGuid():N}";

    private string Uri => $"mcp://{_server}/echo";

    private RouteTelemetryProbe Spans()
        => new(a => a.Kind == ActivityKind.Client
                    && RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)?.Contains(_server, StringComparison.Ordinal) == true);

    private async Task<RouteContext> Start(IMcpClient? client, bool telemetry = true)
    {
        var registry = new McpRegistry();
        if (client is not null) registry.Register(client);
        var ctx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = telemetry });
        ctx.AddComponent(new McpComponent(registry));
        await ctx.Start();
        return ctx;
    }

    private IMcpClient Client(Func<CancellationToken, Task<CallToolResult>> call, McpClientStatus status = McpClientStatus.Healthy)
    {
        var client = Substitute.For<IMcpClient>();
        client.ServerName.Returns(_server);
        client.Status.Returns(status);
        client.CallToolAsync("echo", Arg.Any<JsonNode?>(), Arg.Any<CancellationToken>())
            .Returns(ci => call(ci.ArgAt<CancellationToken>(2)));
        return client;
    }

    private static Task Call(RouteContext ctx, string uri, CancellationToken ct = default)
        => ctx.GetEndpoint(uri).CreateProducer().Process(new Exchange(new Message("{}")), ct);

    [Fact]
    public async Task The_call_is_a_span_of_its_endpoint()
    {
        using var probe = Spans();

        await using (var ctx = await Start(Client(_ => Task.FromResult(new CallToolResult { Content = new JsonArray() }))))
            await Call(ctx, Uri);

        var span = probe.Activities.Should().ContainSingle().Subject;
        span.Status.Should().NotBe(ActivityStatusCode.Error);
        RouteTelemetryProbe.Tag(span, "rpc.system").Should().Be("mcp");
        RouteTelemetryProbe.Tag(span, "rpc.method").Should().Be("echo");
    }

    [Fact]
    public async Task A_failed_call_marks_the_span_red()
    {
        using var probe = Spans();

        await using (var ctx = await Start(Client(_ => throw new McpException("tool failed"))))
        {
            var call = () => Call(ctx, Uri);
            await call.Should().ThrowAsync<McpException>();
        }

        probe.Activities.Should().ContainSingle().Which.Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task An_unknown_server_marks_the_span_red()
    {
        using var probe = Spans();

        await using (var ctx = await Start(client: null))
        {
            var call = () => Call(ctx, Uri);
            await call.Should().ThrowAsync<McpException>();
        }

        probe.Activities.Should().ContainSingle().Which.Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task A_timeout_marks_the_span_red()
    {
        using var probe = Spans();

        await using (var ctx = await Start(Client(async ct =>
                     {
                         await Task.Delay(Timeout.Infinite, ct);
                         return new CallToolResult();
                     })))
        {
            var call = () => Call(ctx, $"{Uri}?callTimeoutMs=100");
            await call.Should().ThrowAsync<TimeoutException>();
        }

        probe.Activities.Should().ContainSingle().Which.Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task Our_own_cancellation_does_not_mark_the_span()
    {
        using var probe = Spans();
        using var cts = new CancellationTokenSource();

        await using (var ctx = await Start(Client(async ct =>
                     {
                         await cts.CancelAsync();
                         await Task.Delay(Timeout.Infinite, ct);
                         return new CallToolResult();
                     })))
        {
            var call = () => Call(ctx, Uri, cts.Token);
            await call.Should().ThrowAsync<OperationCanceledException>();
        }

        probe.Activities.Should().ContainSingle().Which.Status.Should().NotBe(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task Tracing_off_opens_no_span()
    {
        using var probe = new RouteTelemetryProbe(a => RouteTelemetryProbe.Tag(a, "rpc.service") == _server);

        await using (var ctx = await Start(Client(_ => Task.FromResult(new CallToolResult { Content = new JsonArray() })), telemetry: false))
            await Call(ctx, Uri);

        probe.Activities.Should().BeEmpty();
    }
}
