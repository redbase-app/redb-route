using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Soap;
using SoapDsl = redb.Route.Soap.Fluent.Soap;

namespace redb.Route.Tests.Soap;

/// <summary>
/// Ф6 live MTOM interop: an independent MTOM producer (Node.js <c>soap</c>, <c>forceMTOM</c>) ↔ our consumer.
/// Node's <c>soap</c> implements MTOM on the client but not the server, so this is the meaningful direction.
/// Both halves are proven: our <c>multipart/related</c> parser lifts the foreign stack's inbound XOP
/// attachment, and the foreign client parses the MTOM attachment our consumer returns. GATED
/// (<c>Category=Interop</c>): no-ops unless the Node container answers on 127.0.0.1:18080. See
/// <c>C:\Work\yaml\soap</c>.
/// </summary>
[Trait("Category", "Interop")]
public class SoapMtomInteropTests
{
    private const string Host = "127.0.0.1";
    private const int ServerPort = 18080;
    // Distinct from SoapInteropTests' consumer port; offset by runtime major against parallel-TFM contention.
    private static readonly int ConsumerPort = 18110 + Environment.Version.Major;

    [Fact]
    public async Task NodeMtomClient_To_RedbConsumer_CarriesAttachment()
    {
        if (!IsReachable(Host, ServerPort))
            return; // gated: Node SOAP container not running

        byte[]? consumerGot = null;
        var uploaded = Encoding.UTF8.GetBytes("node-upload-bytes");

        await using var ctx = new RouteContext();
        ctx.AddComponent(new SoapComponent());
        ctx.AddToRegistry("mtom", new SoapConnectionFactory { Mtom = true });
        ctx.AddRoutes(r => r.From(SoapDsl.Listen("/svc").Host("0.0.0.0").Port(ConsumerPort).ConnectionFactory("mtom"))
            .Process(e =>
            {
                consumerGot = e.In.GetHeader<IReadOnlyList<SoapAttachment>>(SoapHeaders.Attachments)?[0].Content;
                var msg = XElement.Parse(e.In.Body!.ToString()!).Descendants().First(x => x.Name.LocalName == "msg").Value;
                e.In.Body = $"<tns:EchoResponse xmlns:tns=\"urn:soaptest\"><result>mtom:{msg}</result></tns:EchoResponse>";
                e.In.Headers[SoapHeaders.Attachments] =
                    new List<SoapAttachment> { new("resp-1", "application/octet-stream", Encoding.UTF8.GetBytes("redb-mtom-reply")) };
            }));
        await ctx.Start();

        var (stdout, stderr, exit) = RunNodeMtomClient("hi-mtom", "node-upload-bytes");

        exit.Should().Be(0, $"node mtom client should succeed; stderr='{stderr}'");
        stdout.Should().Contain("RESULT:mtom:hi-mtom");
        // Inbound proof: our multipart/related parser lifted the foreign client's XOP attachment intact.
        consumerGot.Should().Equal(uploaded);
        // Outbound proof: the foreign client parsed the MTOM attachment our consumer returned.
        stdout.Should().Contain("RESPATT:redb-mtom-reply");
    }

    private static (string stdout, string stderr, int exit) RunNodeMtomClient(string msg, string attBody)
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
        psi.ArgumentList.Add($"TARGET=http://host.docker.internal:{ConsumerPort}/svc node /app/mtom-client.js {msg} {attBody}");

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
