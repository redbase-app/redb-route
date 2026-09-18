using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using Grpc.Core;
using Grpc.Net.Client;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Grpc;
using redb.Route.Http;

namespace redb.Route.Tests.Grpc;

/// <summary>
/// The caller's identity on gRPC exchanges: the shared host's resolver identifies the call from its
/// metadata, and the consumer puts the principal on the exchange (<see cref="ExchangePrincipal"/>).
/// </summary>
public sealed class GrpcPrincipalTests : IAsyncLifetime
{
    private const string MethodPath = "/probe.v1.Probe/Who";

    private int _port;
    private SharedHttpServerManager _manager = null!;
    private RouteContext _context = null!;

    public async Task InitializeAsync()
    {
        _port = GetFreePort();
        _manager = new SharedHttpServerManager(new HttpHostingOptions
        {
            ResolvePrincipal = ctx => Task.FromResult(ctx.Request.Headers["x-test-token"].ToString() == "good"
                ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "grpc-user")], "test"))
                : null),
        });

        _context = new RouteContext();
        _context.AddComponent(new GrpcComponent { ServerManager = _manager });
        _context.AddRoutes(r => r.From(GrpcDsl.Listen($"127.0.0.1:{_port}").Method(MethodPath))
            .Process(e => e.Out = new Message(
                ExchangePrincipal.Get(e)?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "anonymous")));
        await _context.Start();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _manager.DisposeAsync();
    }

    [Fact]
    public async Task IdentifiedCall_PrincipalReachesTheExchange()
    {
        (await Call("good")).Should().Be("grpc-user");
    }

    [Fact]
    public async Task AnonymousCall_CarriesNoIdentity()
    {
        (await Call(null)).Should().Be("anonymous");
    }

    private async Task<string> Call(string? token)
    {
        using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{_port}");

        var slash = MethodPath.LastIndexOf('/');
        var marshaller = Marshallers.Create(b => (byte[])b, b => b);
        var method = new Method<byte[], byte[]>(
            MethodType.Unary, MethodPath[1..slash], MethodPath[(slash + 1)..], marshaller, marshaller);

        var metadata = new Metadata();
        if (token is not null) metadata.Add("x-test-token", token);

        var reply = await channel.CreateCallInvoker()
            .AsyncUnaryCall(method, null, new CallOptions(metadata), Array.Empty<byte>());

        return Encoding.UTF8.GetString(reply);
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
