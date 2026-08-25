using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Core;
using redb.Route.Grpc;
using redb.Route.Grpc.Proto;

namespace redb.Route.Tests.Grpc;

/// <summary>
/// End-to-end through the real engine: routes declared with the DSL inside a <see cref="RouteContext"/>,
/// started by the context, called by a real gRPC client. The consumer-level tests build endpoints by
/// hand; this one proves the same thing the way a module actually wires it — including that several gRPC
/// routes in one context share a single Kestrel host instead of fighting over the port.
/// </summary>
public sealed class GrpcRouteEngineTests : IAsyncLifetime
{
    private ServiceProvider _sp = null!;
    private RouteContext _context = null!;
    private int _port;

    public Task InitializeAsync()
    {
        _port = GetFreePort();

        var services = new ServiceCollection();
        services.AddRedbRouteGrpc();                 // registers the component + shared Kestrel host
        _sp = services.BuildServiceProvider();

        _context = new RouteContext(_sp, "grpc-engine-test");
        _context.AddComponent(new GrpcComponent
        {
            ServerManager = _sp.GetRequiredService<redb.Route.Http.SharedHttpServerManager>(),
        });

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsyncCore()
    {
        await _context.DisposeAsync();
        await _sp.DisposeAsync();
    }

    public async Task DisposeAsync() => await DisposeAsyncCore();

    [Fact]
    public async Task Two_dsl_routes_share_one_port_and_answer_by_method_address()
    {
        _context.AddRoutes(r =>
        {
            r.From(GrpcDsl.Listen($"127.0.0.1:{_port}").Method("/identity.v1.Identity/Token"))
                .RouteId("grpc-token")
                .Process(e => e.Out = new Message("token-reply"));
        });

        _context.AddRoutes(r =>
        {
            r.From(GrpcDsl.Listen($"127.0.0.1:{_port}").Method("/identity.v1.Identity/Introspect"))
                .RouteId("grpc-introspect")
                .Process(e => e.Out = new Message("introspect-reply"));
        });

        await _context.Start();

        (await Call("/identity.v1.Identity/Token")).Should().Be("token-reply");
        (await Call("/identity.v1.Identity/Introspect")).Should().Be("introspect-reply");

        // Each address is its own route, with its own id and lifecycle — the point of the whole design.
        _context.Routes.Select(x => x.RouteId).Should().Contain(["grpc-token", "grpc-introspect"]);
    }

    [Fact]
    public async Task Generic_service_route_still_works_through_the_dsl()
    {
        _context.AddRoutes(r =>
        {
            r.From(GrpcDsl.Listen($"127.0.0.1:{_port}"))
                .RouteId("grpc-generic")
                .Process(e => e.Out = new Message("generic-reply"));
        });

        await _context.Start();

        using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{_port}");
        var reply = await new RedbService.RedbServiceClient(channel)
            .ProcessAsync(new RedbMessage { Payload = ByteString.CopyFromUtf8("ping") });

        reply.Payload.ToStringUtf8().Should().Be("generic-reply");
    }

    private async Task<string> Call(string methodPath)
    {
        using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{_port}");

        var slash = methodPath.LastIndexOf('/');
        var marshaller = Marshallers.Create(b => (byte[])b, b => b);
        var method = new Method<byte[], byte[]>(
            MethodType.Unary, methodPath[1..slash], methodPath[(slash + 1)..], marshaller, marshaller);

        var reply = await channel.CreateCallInvoker()
            .AsyncUnaryCall(method, null, new CallOptions(), Array.Empty<byte>());

        return System.Text.Encoding.UTF8.GetString(reply);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
