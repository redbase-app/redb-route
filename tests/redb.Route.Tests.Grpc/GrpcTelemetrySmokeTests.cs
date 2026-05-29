using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using OpenTelemetry;
using OpenTelemetry.Trace;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Grpc;
using redb.Route.Telemetry;

namespace redb.Route.Tests.Grpc;

/// <summary>Smoke test for the P1 transport span opened by <see cref="GrpcProducer"/>.</summary>
public sealed class GrpcTelemetrySmokeTests : IAsyncLifetime
{
    private GrpcConsumer? _consumer;
    private GrpcProducer? _producer;
    private int _port;

    public Task InitializeAsync()
    {
        _port = GetFreePort();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_producer is not null) await _producer.Stop();
        if (_consumer is not null) await _consumer.Stop();
    }

    [Fact]
    public async Task GrpcProducer_EmitsTransportSpanWithRpcTags()
    {
        var component = new GrpcComponent();

        // Server
        var serverPars = new Dictionary<string, string>
        {
            ["host"] = "127.0.0.1",
            ["port"] = _port.ToString(),
            ["inOut"] = "true"
        };
        var serverUri = new EndpointUri("grpc", $"/127.0.0.1:{_port}", $"grpc:127.0.0.1:{_port}", serverPars);
        var serverEndpoint = (GrpcEndpoint)component.CreateEndpoint(serverUri);
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci => { ci.Arg<IExchange>().Out = new Message("ack"); return Task.CompletedTask; });
        _consumer = new GrpcConsumer(serverEndpoint, processor, serverEndpoint.EndpointOptions);
        await _consumer.Start();

        // Client
        var clientPars = new Dictionary<string, string> { ["plaintext"] = "true" };
        var clientUri = new EndpointUri("grpc", $"/127.0.0.1:{_port}", $"grpc:127.0.0.1:{_port}", clientPars);
        var clientEndpoint = (GrpcEndpoint)component.CreateEndpoint(clientUri);
        _producer = new GrpcProducer(clientEndpoint, clientEndpoint.EndpointOptions);

        var activities = new List<Activity>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddSource(RouteActivitySource.SourceName)
            .AddInMemoryExporter(activities)
            .Build()!;

        await _producer.Start();
        await _producer.Process(new Exchange(new Message("hello")));

        tracer.ForceFlush(1000);
        activities.Should().NotBeEmpty();
        var activity = activities.First();
        activity.Source.Name.Should().Be(RouteActivitySource.SourceName);
        activity.Kind.Should().Be(ActivityKind.Client);
        activity.GetTagItem("rpc.system").Should().Be("grpc");
        activity.GetTagItem("redb.route.endpoint").Should().NotBeNull();
    }

    private static int GetFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
