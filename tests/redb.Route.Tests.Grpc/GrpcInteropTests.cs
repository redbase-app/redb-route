using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Grpc;

namespace redb.Route.Tests.Grpc;

/// <summary>
/// Live interop against an independent gRPC stack — <c>@grpc/grpc-js</c>, which shares no code with this
/// connector (see <c>C:\Work\yaml\grpc</c>). This is what loopback e2e cannot prove: that a foreign
/// implementation accepts our hand-written framing, trailers, statuses, mTLS handshake and gzip, and that
/// we accept its. It is also the only cross-process coverage — the client runs inside the container.
/// <para>
/// The contract is a <b>typed</b> <c>.proto</c> (<c>interop.Echo</c>), not the generic <c>RedbService</c>,
/// so it doubles as proof that a generated client can call a redb route with no server stubs on our side.
/// Messages are encoded by hand here (one string field) to keep the test free of generated code.
/// </para>
/// <para>
/// GATED: every test no-ops unless the container answers on 127.0.0.1:18100, so the normal suite stays
/// green without it. Run with <c>--filter Category=Interop</c> after <c>docker compose up -d</c> in
/// <c>C:\Work\yaml\grpc</c>.
/// </para>
/// </summary>
[Trait("Category", "Interop")]
public class GrpcInteropTests
{
    private const string Host = "127.0.0.1";
    private const int ServerPort = 18100;                  // @grpc/grpc-js echo server (compose mapping)
    private const string CertDir = @"C:\Work\yaml\grpc\certs";

    // Our consumers; the container reaches them via host-gateway. Offset by runtime major so the parallel
    // multi-TFM test hosts (net8/net9/net10) never contend for the same port.
    private static readonly int PlainPort = 18110 + Environment.Version.Major;
    private static readonly int TlsPort = 18120 + Environment.Version.Major;
    private static readonly int StreamPort = 18130 + Environment.Version.Major;
    private static readonly int StatusPort = 18140 + Environment.Version.Major;

    // ── Direction A: our producer → the independent grpc-js server ──

    [Fact]
    public async Task Redb_producer_to_grpcjs_server_echoes()
    {
        if (!IsReachable(Host, ServerPort)) return;        // gated: see C:\Work\yaml\grpc

        await using var ctx = NewContext();
        ctx.AddRoutes(r => r.From("direct://call")
            .To(GrpcDsl.Call($"{Host}:{ServerPort}").Method("/interop.Echo/Say").Plaintext()));
        await ctx.Start();

        var producer = ctx.GetEndpoint("direct://call").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message(EncodeString("hello-redb")));
        await producer.Process(exchange);

        // A foreign gRPC server parsed our request frame, ran Say, and we read its reply frame back.
        exchange.HasOut.Should().BeTrue();
        DecodeString((byte[])exchange.Out!.Body!).Should().Be("echo:hello-redb");
    }

    [Fact]
    public async Task Redb_producer_reads_a_grpcjs_server_stream()
    {
        if (!IsReachable(Host, ServerPort)) return;

        await using var ctx = NewContext();
        ctx.AddRoutes(r => r.From("direct://stream")
            .To(GrpcDsl.Call($"{Host}:{ServerPort}").Method("/interop.Echo/SayMany").Plaintext().Streaming()));
        await ctx.Start();

        var producer = ctx.GetEndpoint("direct://stream").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message(EncodeString("go")));
        await producer.Process(exchange);

        var items = new List<string>();
        await foreach (var item in (IAsyncEnumerable<object?>)exchange.Out!.Body!)
            items.Add(DecodeString((byte[])item!));

        items.Should().Equal("page-1", "page-2", "page-3");
    }

    // ── Direction B: the independent grpc-js client → our consumer ──

    [Fact]
    public async Task Grpcjs_client_to_redb_consumer_echoes()
    {
        if (!IsReachable(Host, ServerPort)) return;

        await using var ctx = NewContext();
        ctx.AddRoutes(r => r.From(GrpcDsl.Listen($"0.0.0.0:{PlainPort}").Method("/interop.Echo/Say"))
            .Process(e => e.Out = new Message(EncodeString("echo:" + DecodeString((byte[])e.In.Body!)))));
        await ctx.Start();

        var result = RunClient($"host.docker.internal:{PlainPort}", "say");

        result.GetProperty("ok").GetBoolean().Should().BeTrue();
        result.GetProperty("value").GetString().Should().Be("echo:hello-redb");
    }

    [Fact]
    public async Task Grpcjs_client_gets_a_real_status_from_our_consumer()
    {
        if (!IsReachable(Host, ServerPort)) return;

        await using var ctx = NewContext();
        ctx.AddRoutes(r => r.From(GrpcDsl.Listen($"0.0.0.0:{StatusPort}").Method("/interop.Echo/Say"))
            .Process(e =>
            {
                e.Out = new Message(Array.Empty<byte>());
                e.Out.Headers["status.code"] = 403;        // what a controller dispatcher writes
                e.Out.Headers[GrpcHeaders.StatusDetail] = "scope required";
            }));
        await ctx.Start();

        var result = RunClient($"host.docker.internal:{StatusPort}", "say");

        result.GetProperty("ok").GetBoolean().Should().BeFalse();
        result.GetProperty("code").GetInt32().Should().Be(7);   // PERMISSION_DENIED
        result.GetProperty("details").GetString().Should().Be("scope required");
    }

    [Fact]
    public async Task Grpcjs_client_reads_our_server_stream()
    {
        if (!IsReachable(Host, ServerPort)) return;

        await using var ctx = NewContext();
        ctx.AddRoutes(r => r.From(GrpcDsl.Listen($"0.0.0.0:{StreamPort}")
                .Method("/interop.Echo/SayMany").Streaming())
            .Process(e => e.Out = new Message(Pages())));
        await ctx.Start();

        var result = RunClient($"host.docker.internal:{StreamPort}", "saymany");

        result.GetProperty("ok").GetBoolean().Should().BeTrue();
        result.GetProperty("value").EnumerateArray().Select(x => x.GetString())
            .Should().Equal("page-1", "page-2", "page-3");

        static async IAsyncEnumerable<object?> Pages()
        {
            for (var i = 1; i <= 3; i++)
            {
                await Task.Yield();
                yield return EncodeString($"page-{i}");
            }
        }
    }

    [Fact]
    public async Task Grpcjs_client_over_mtls_is_authenticated_by_thumbprint()
    {
        if (!IsReachable(Host, ServerPort)) return;
        if (!File.Exists(Path.Combine(CertDir, "server.pfx"))) return;   // gated: certs not generated

        var clientThumbprint = ThumbprintOf(Path.Combine(CertDir, "client.crt"));

        await using var ctx = NewContext();
        ctx.AddRoutes(r => r.From(GrpcDsl.Listen($"0.0.0.0:{TlsPort}")
                .Method("/interop.Echo/Say")
                .Ssl()
                .SslCertPath(Path.Combine(CertDir, "server.pfx"))
                .SslCertPassword("redb")
                .ClientCertificates(GrpcClientCertificateMode.RequireCertificate, clientThumbprint))
            .Process(e => e.Out = new Message(EncodeString(
                "mtls:" + e.In.GetHeader<string>(GrpcHeaders.ClientCertSubject)))));
        await ctx.Start();

        var result = RunClient($"host.docker.internal:{TlsPort}", "say", "--tls");

        // A real TLS handshake with a client certificate, driven by a foreign stack, and the certificate
        // surfaced to the route.
        result.GetProperty("ok").GetBoolean().Should().BeTrue();
        result.GetProperty("value").GetString().Should().Contain("redb-interop-client");
    }

    [Fact]
    public async Task Grpcjs_client_may_gzip_its_request()
    {
        if (!IsReachable(Host, ServerPort)) return;

        var port = PlainPort + 100;
        await using var ctx = NewContext();
        ctx.AddRoutes(r => r.From(GrpcDsl.Listen($"0.0.0.0:{port}")
                .Method("/interop.Echo/Say").Compression(GrpcCompression.Gzip))
            .Process(e => e.Out = new Message(EncodeString("gz:" + DecodeString((byte[])e.In.Body!)))));
        await ctx.Start();

        var result = RunClient($"host.docker.internal:{port}", "say", "--gzip", "--msg=" + new string('c', 512));

        result.GetProperty("ok").GetBoolean().Should().BeTrue();
        result.GetProperty("value").GetString().Should().Be("gz:" + new string('c', 512));
    }

    // ── helpers ───────────────────────────────────────────────

    private static RouteContext NewContext()
    {
        var ctx = new RouteContext();
        ctx.AddComponent(new GrpcComponent());
        return ctx;
    }

    /// <summary>Runs the container's grpc-js client and returns its JSON verdict.</summary>
    private static JsonElement RunClient(string target, string method, params string[] flags)
    {
        var args = $"exec grpc-echo node client.js {target} {method} {string.Join(' ', flags)}";
        using var process = Process.Start(new ProcessStartInfo("docker", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);

        var line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(l => l.TrimStart().StartsWith('{'));

        line.Should().NotBeNull($"client produced no verdict. stdout={stdout} stderr={stderr}");
        return JsonDocument.Parse(line!).RootElement.Clone();
    }

    /// <summary>Encodes <c>{ string field 1 }</c> — the whole schema of the interop messages.</summary>
    private static byte[] EncodeString(string value)
    {
        var utf8 = Encoding.UTF8.GetBytes(value);
        var length = new List<byte>();
        var remaining = (uint)utf8.Length;
        do
        {
            var chunk = (byte)(remaining & 0x7F);
            remaining >>= 7;
            if (remaining > 0) chunk |= 0x80;
            length.Add(chunk);
        } while (remaining > 0);

        var result = new byte[1 + length.Count + utf8.Length];
        result[0] = 0x0A;                                   // field 1, wire type 2 (length-delimited)
        length.CopyTo(result, 1);
        utf8.CopyTo(result, 1 + length.Count);
        return result;
    }

    private static string DecodeString(byte[] message)
    {
        if (message.Length == 0) return string.Empty;
        message[0].Should().Be(0x0A);

        var offset = 1;
        var length = 0;
        var shift = 0;
        while (true)
        {
            var b = message[offset++];
            length |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return Encoding.UTF8.GetString(message, offset, length);
    }

    private static string ThumbprintOf(string pemPath)
    {
        var cert = System.Security.Cryptography.X509Certificates.X509Certificate2
            .CreateFromPem(File.ReadAllText(pemPath));
        return cert.Thumbprint;
    }

    private static bool IsReachable(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(host, port).Wait(TimeSpan.FromMilliseconds(750)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

}
