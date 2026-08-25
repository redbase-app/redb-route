using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Http;
using redb.Route.Grpc.Proto;
using redb.Route.Http;
using RouteHttpProtocol = redb.Route.Http.HttpProtocol;

namespace redb.Route.Tests.Grpc;

/// <summary>
/// PROBE (G1 option C): can we serve gRPC as a plain path route on the shared Kestrel host,
/// with our own wire framing, instead of pulling in the Grpc.AspNetCore server stack?
/// <para>
/// The whole point is the client side: every test here drives a <b>real</b>
/// <see cref="GrpcChannel"/> + generated <c>RedbServiceClient</c>. If our framing were wrong the
/// client would fail, so a green run is interop evidence, not a self-consistent mock.
/// </para>
/// <para>
/// Wire format under test (gRPC over HTTP/2, unary):
/// request/response body = <c>[1 byte compressed-flag][4 bytes length BE][message]</c>,
/// content-type <c>application/grpc</c>, HTTP status always 200, real status in the
/// <c>grpc-status</c> / <c>grpc-message</c> trailers, deadline arrives as <c>grpc-timeout</c>.
/// </para>
/// </summary>
public sealed class GrpcRawFramingProbeTests : IAsyncLifetime
{
    private const string Host = "127.0.0.1";
    private const string ProcessPath = "/redb.route.grpc.RedbService/Process";
    private const string GrpcContentType = "application/grpc";

    private readonly SharedHttpServerManager _server = new();
    private int _port;

    public Task InitializeAsync()
    {
        _port = GetFreePort();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    // ── Probe 1: does a real gRPC client accept our hand-rolled unary response? ──

    [Fact]
    public async Task Unary_RoundTrip_Works_Against_A_Real_Grpc_Client()
    {
        await Serve(async http =>
        {
            var request = await ReadMessage(http, RedbMessage.Parser);
            var reply = new RedbMessage
            {
                Payload = ByteString.CopyFromUtf8("pong:" + request.Payload.ToStringUtf8()),
            };
            reply.Headers["handled-by"] = "raw-framing";
            reply.Headers["echo-operation"] = request.Headers["operation"];
            await WriteMessage(http, reply, StatusCode.OK);
        });

        var client = CreateClient();

        var request = new RedbMessage { Payload = ByteString.CopyFromUtf8("ping") };
        request.Headers["operation"] = "echo";

        var reply = await client.ProcessAsync(request);

        reply.Payload.ToStringUtf8().Should().Be("pong:ping");
        reply.Headers["handled-by"].Should().Be("raw-framing");
        reply.Headers["echo-operation"].Should().Be("echo");
    }

    // ── Probe 2: can we return a real gRPC status (the thing G2 needs)? ──

    [Fact]
    public async Task Trailers_Only_Error_Surfaces_As_RpcException_With_The_Right_Status()
    {
        await Serve(http => WriteMessage(http, null, StatusCode.PermissionDenied, "scope 'identity:manage' required"));

        var client = CreateClient();

        var act = async () => await client.ProcessAsync(new RedbMessage());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        ex.Which.Status.Detail.Should().Be("scope 'identity:manage' required");
    }

    // ── Probe 3: does the client's deadline reach us as a header we can honour? ──

    [Fact]
    public async Task Client_Deadline_Arrives_As_Grpc_Timeout_Header()
    {
        string? timeout = null;
        await Serve(async http =>
        {
            timeout = http.Request.Headers["grpc-timeout"].ToString();
            await ReadMessage(http, RedbMessage.Parser);
            await WriteMessage(http, new RedbMessage(), StatusCode.OK);
        });

        var client = CreateClient();

        await client.ProcessAsync(new RedbMessage(), deadline: DateTime.UtcNow.AddSeconds(30));

        timeout.Should().NotBeNullOrEmpty("the client must send its deadline as grpc-timeout");
        // Format is <value><unit>, e.g. "29999m" (milliseconds) or "30S".
        timeout.Should().MatchRegex("^[0-9]+[HMSmun]$");
    }

    // ── Probe 4: many methods on one port — the reason for option C in the first place ──

    [Fact]
    public async Task Two_Methods_On_One_Port_Are_Routed_By_Path()
    {
        _server.RegisterRoute(Host, _port, "/identity.v1.Identity/Token", "POST",
            http => Reply(http, "token"), protocol: RouteHttpProtocol.Http2);
        _server.RegisterRoute(Host, _port, "/identity.v1.Identity/Introspect", "POST",
            http => Reply(http, "introspect"), protocol: RouteHttpProtocol.Http2);
        await _server.EnsureStarted(Host, _port);

        using var channel = GrpcChannel.ForAddress($"http://{Host}:{_port}");

        (await CallRaw(channel, "/identity.v1.Identity/Token")).Should().Be("token");
        (await CallRaw(channel, "/identity.v1.Identity/Introspect")).Should().Be("introspect");

        static async Task Reply(HttpContext http, string who)
        {
            await ReadMessage(http, RedbMessage.Parser);
            await WriteMessage(http, new RedbMessage { Payload = ByteString.CopyFromUtf8(who) }, StatusCode.OK);
        }
    }

    // ── helpers ───────────────────────────────────────────────

    private async Task Serve(Func<HttpContext, Task> handler)
    {
        _server.RegisterRoute(Host, _port, ProcessPath, "POST", handler, protocol: RouteHttpProtocol.Http2);
        await _server.EnsureStarted(Host, _port);
    }

    private RedbService.RedbServiceClient CreateClient()
        => new(GrpcChannel.ForAddress($"http://{Host}:{_port}"));

    /// <summary>Calls an arbitrary method path with the generic message, bypassing generated stubs.</summary>
    private static async Task<string> CallRaw(GrpcChannel channel, string fullMethodPath)
    {
        var slash = fullMethodPath.LastIndexOf('/');
        var method = new Method<RedbMessage, RedbMessage>(
            MethodType.Unary,
            fullMethodPath[1..slash],
            fullMethodPath[(slash + 1)..],
            Marshallers.Create(m => m.ToByteArray(), RedbMessage.Parser.ParseFrom),
            Marshallers.Create(m => m.ToByteArray(), RedbMessage.Parser.ParseFrom));

        var reply = await channel.CreateCallInvoker()
            .AsyncUnaryCall(method, null, new CallOptions(), new RedbMessage());
        return reply.Payload.ToStringUtf8();
    }

    private static async Task<T> ReadMessage<T>(HttpContext http, MessageParser<T> parser) where T : IMessage<T>
    {
        using var buffer = new MemoryStream();
        await http.Request.Body.CopyToAsync(buffer, http.RequestAborted);
        var bytes = buffer.ToArray();

        bytes.Length.Should().BeGreaterThanOrEqualTo(5, "a gRPC frame carries a 5-byte prefix");
        bytes[0].Should().Be(0, "compressed frames are out of scope for the probe");

        var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1, 4));
        return parser.ParseFrom(bytes.AsSpan(5, (int)length).ToArray());
    }

    private static async Task WriteMessage(HttpContext http, IMessage? message, StatusCode status, string? detail = null)
    {
        http.Response.ContentType = GrpcContentType;

        if (message is not null)
        {
            var payload = message.ToByteArray();
            var frame = new byte[5 + payload.Length];
            frame[0] = 0;
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1, 4), (uint)payload.Length);
            payload.CopyTo(frame, 5);

            await http.Response.Body.WriteAsync(frame, http.RequestAborted);
            await http.Response.Body.FlushAsync(http.RequestAborted);
        }

        http.Response.AppendTrailer("grpc-status", ((int)status).ToString());
        if (!string.IsNullOrEmpty(detail))
            http.Response.AppendTrailer("grpc-message", Uri.EscapeDataString(detail));
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
