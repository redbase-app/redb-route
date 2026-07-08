using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Trace;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;
using redb.Route.Telemetry;

namespace redb.Route.Tests.Http;

/// <summary>
/// Smoke test: verifies <see cref="HttpProducer"/> opens a transport span on
/// <see cref="RouteActivitySource.Source"/> with the OpenTelemetry semantic
/// HTTP attributes after P1 telemetry instrumentation.
/// </summary>
public sealed class HttpTelemetrySmokeTests : IAsyncLifetime
{
    private WebApplication? _server;
    private int _port;

    public async Task InitializeAsync()
    {
        _port = GetFreePort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, _port));
        builder.Logging.ClearProviders();
        _server = builder.Build();
        _server.Map("/{**catch}", static async ctx =>
        {
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync("ok");
        });
        await _server.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.StopAsync();
            await _server.DisposeAsync();
        }
    }

    [Fact]
    public async Task HttpProducer_EmitsTransportSpanWithHttpSemantics()
    {
        var activities = new List<Activity>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddSource(RouteActivitySource.SourceName)
            .AddInMemoryExporter(activities)
            .Build()!;

        var component = new HttpComponent();
        var path = $"/localhost:{_port}/smoke";
        var uri = new EndpointUri("http", path, $"http:{path}", new Dictionary<string, string>());
        var endpoint = (HttpEndpoint)component.CreateEndpoint(uri);

        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);
        await producer.Start();
        try
        {
            await producer.Process(new Exchange(new Message(null)));
        }
        finally
        {
            await producer.Stop();
        }

        tracer.ForceFlush(1000);
        activities.Should().NotBeEmpty("HttpProducer.Process must open a transport span");
        var activity = activities.Single();
        activity.Source.Name.Should().Be(RouteActivitySource.SourceName);
        activity.Kind.Should().Be(ActivityKind.Client);
        activity.GetTagItem("http.method").Should().Be("GET");
        activity.GetTagItem("redb.route.endpoint").Should().NotBeNull();
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
