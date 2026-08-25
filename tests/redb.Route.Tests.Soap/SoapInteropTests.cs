using System.Diagnostics;
using System.Net.Sockets;
using System.Xml.Linq;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using SoapDsl = redb.Route.Soap.Fluent.Soap;

namespace redb.Route.Tests.Soap;

/// <summary>
/// Ф8 live interop against an independent SOAP stack — the Node.js <c>soap</c> library, a mature
/// implementation with no shared code with this connector (see <c>C:\Work\yaml\soap</c>). This proves what
/// loopback e2e cannot: that a foreign SOAP implementation accepts our on-the-wire envelope, and that we
/// accept its. CoreWCF was rejected as the reference stack because every version carries an unpatched
/// crypto CVE; a JVM/CXF image is heavier but equivalent — Node was chosen only because its base image was
/// already cached. GATED: each test no-ops unless the container answers on 127.0.0.1:18080, so the normal
/// suite stays green without it. Run with <c>--filter Category=Interop</c> after
/// <c>docker compose up -d</c> in <c>C:\Work\yaml\soap</c>.
/// </summary>
[Trait("Category", "Interop")]
public class SoapInteropTests
{
    private const string Host = "127.0.0.1";
    private const int ServerPort = 18080;                 // Node SOAP echo server (compose port mapping)
    // Our consumer; the container reaches it via host-gateway. Offset by runtime major so the parallel
    // multi-TFM test hosts (net8/net9/net10) never contend for the same port.
    private static readonly int ConsumerPort = 18090 + Environment.Version.Major;
    private static string ServerUrl => $"http://{Host}:{ServerPort}/echo";

    /// <summary>Direction A: our producer → the independent Node SOAP server. Proves a foreign stack accepts our wire.</summary>
    [Fact]
    public async Task Redb_Producer_To_NodeSoapServer_Echoes()
    {
        if (!IsReachable(Host, ServerPort))
            return; // gated: Node SOAP server not running — see C:\Work\yaml\soap\docker-compose.yml

        await using var ctx = new RouteContext();
        ctx.AddComponent(new redb.Route.Soap.SoapComponent());
        ctx.AddRoutes(r => r.From("direct://call")
            .To(SoapDsl.Call(ServerUrl).Action("urn:soaptest/Echo")));
        await ctx.Start();

        var producer = ctx.GetEndpoint("direct://call").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message(
            "<EchoRequest xmlns=\"urn:soaptest\"><msg>hello-redb</msg></EchoRequest>"));
        await producer.Process(exchange);

        // The Node service parsed our envelope, ran Echo, and returned a response our parser read back.
        exchange.HasOut.Should().BeTrue();
        var result = XElement.Parse(exchange.Out!.Body!.ToString()!)
            .Descendants().First(x => x.Name.LocalName == "result").Value;
        result.Should().Be("echo:hello-redb");
    }

    /// <summary>Direction B: the independent Node SOAP client → our consumer. Proves a foreign client accepts our responses.</summary>
    [Fact]
    public async Task NodeSoapClient_To_RedbConsumer_Echoes()
    {
        if (!IsReachable(Host, ServerPort))
            return; // gated: Node SOAP container not running

        await using var ctx = new RouteContext();
        ctx.AddComponent(new redb.Route.Soap.SoapComponent());
        // Bind on all interfaces so the container's host-gateway can reach us.
        ctx.AddRoutes(r => r.From(SoapDsl.Listen("/svc").Host("0.0.0.0").Port(ConsumerPort))
            .Process(e =>
            {
                var msg = XElement.Parse(e.In.Body!.ToString()!)
                    .Descendants().First(x => x.Name.LocalName == "msg").Value;
                // Match the WSDL response shape (EchoResponse qualified, result unqualified) so the Node client parses it.
                e.In.Body = $"<tns:EchoResponse xmlns:tns=\"urn:soaptest\"><result>redb:{msg}</result></tns:EchoResponse>";
            }));
        await ctx.Start();

        var (stdout, stderr, exit) = RunNodeClient("hi-from-node");

        exit.Should().Be(0, $"node client should succeed; stderr='{stderr}'");
        stdout.Should().Contain("RESULT:redb:hi-from-node");
    }

    private static (string stdout, string stderr, int exit) RunNodeClient(string msg)
    {
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add("soap-echo");
        psi.ArgumentList.Add("sh");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add($"TARGET=http://host.docker.internal:{ConsumerPort}/svc node /app/client.js {msg}");

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return (stdout, stderr, p.HasExited ? p.ExitCode : -1);
    }

    private static bool IsReachable(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(host, port);
            return connect.Wait(TimeSpan.FromMilliseconds(500)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
