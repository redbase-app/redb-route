using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Google.Protobuf;
using Grpc.Net.Client;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Grpc;
using redb.Route.Grpc.Proto;

namespace redb.Route.Tests.Grpc;

/// <summary>
/// Stopping a gRPC route must wait for the calls it is already serving. It used to unregister its
/// routes and ask the listener to stop when empty, and a listener shared with another route stays up
/// — so nobody waited and a deploy cut the call mid-pipeline. Reported by the AS4 agent 2026-09-25;
/// the drain is the engine's <see cref="InflightDrainGuard"/>.
/// </summary>
public class GrpcStopDrainsInflightTests
{
    private static int FreePort() => global::redb.Route.Tests.Shared.TestPorts.Next();

    [Fact]
    public async Task Stop_waits_for_a_call_already_in_the_pipeline()
    {
        var port = FreePort();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;

        var component = new GrpcComponent();
        var uri = new EndpointUri("grpc", $"/127.0.0.1:{port}", $"grpc:127.0.0.1:{port}",
            new Dictionary<string, string> { ["host"] = "127.0.0.1", ["port"] = port.ToString() });
        var endpoint = (GrpcEndpoint)component.CreateEndpoint(uri);

        var consumer = new GrpcConsumer(endpoint, new redb.Route.Processors.DelegateProcessor(async (e, ct) =>
        {
            entered.TrySetResult();
            await release.Task;
            finished = true;
            e.Out = new Message(ByteString.CopyFromUtf8("ok").ToByteArray());
        }), endpoint.EndpointOptions);

        await consumer.Start();

        using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}",
            new GrpcChannelOptions { HttpHandler = new SocketsHttpHandler { EnableMultipleHttp2Connections = true } });
        var client = new RedbService.RedbServiceClient(channel);

        var call = client.ProcessAsync(new RedbMessage { Payload = ByteString.CopyFromUtf8("payload") }).ResponseAsync;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var stop = consumer.Stop();
        (await Task.WhenAny(stop, Task.Delay(500)) == stop)
            .Should().BeFalse("Stop must not return while a call is still inside the pipeline");

        release.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(15));
        finished.Should().BeTrue();

        await call;
    }
}
