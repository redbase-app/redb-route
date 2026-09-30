using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FluentAssertions;
using MimeKit;
using redb.Route.Abstractions;
using redb.Route.As2;
using redb.Route.As2.Crypto;
using redb.Route.Core;
using As2Dsl = redb.Route.As2.Fluent.As2;

namespace redb.Route.Tests.As2;

/// <summary>
/// Stopping one route on a shared listener must wait for the deliveries it is already processing. It
/// used to unregister the route and ask the listener to stop when empty — and when another route
/// still holds the port, the listener stays up, so a partner's message could be cut between decrypt
/// and route. Reported by the AS4 agent 2026-09-25.
/// </summary>
public class As2StopDrainsInflightTests
{
    private static readonly X509Certificate2 Cert = MakeCert("as2-drain");

    private static int FreePort() => global::redb.Route.Tests.Shared.TestPorts.Next();

    private static X509Certificate2 MakeCert(string cn)
    {
        var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certWithKey = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
#pragma warning disable SYSLIB0057
        using var publicOnly = new X509Certificate2(certWithKey.Export(X509ContentType.Cert));
#pragma warning restore SYSLIB0057
        return publicOnly.CopyWithPrivateKey(rsa);
    }

    private static As2ConnectionFactory Factory() => new()
    {
        OurCertificate = Cert,
        PartnerCertificate = Cert,
        As2From = "US", As2To = "THEM",
        Sign = true, Encrypt = true, SignedMdn = true, MdnMode = As2MdnMode.Sync,
    };

    private static (string contentType, string transferEncoding, byte[] body) BuildAs2Message(string payload)
    {
        var engine = new As2CryptoEngine();
        var part = new MimePart("application", "edi-x12")
        {
            Content = new MimeContent(new MemoryStream(Encoding.UTF8.GetBytes(payload))),
            ContentTransferEncoding = ContentEncoding.Binary,
        };
        var signed = engine.Sign(part, Cert, "sha-256");
        var encrypted = engine.Encrypt(signed, Cert, "aes-128-cbc");

        using var ms = new MemoryStream();
        var options = FormatOptions.Default.Clone();
        options.NewLineFormat = NewLineFormat.Dos;
        encrypted.WriteTo(options, ms);
        var all = ms.ToArray();

        var sep = -1;
        for (var i = 0; i + 3 < all.Length; i++)
            if (all[i] == 13 && all[i + 1] == 10 && all[i + 2] == 13 && all[i + 3] == 10) { sep = i; break; }
        var body = sep >= 0 ? all[(sep + 4)..] : all;

        return (encrypted.Headers[HeaderId.ContentType]!, encrypted.Headers[HeaderId.ContentTransferEncoding]!, body);
    }

    [Fact]
    public async Task Stop_waits_for_a_delivery_already_in_the_pipeline_while_the_port_stays_busy()
    {
        var port = FreePort();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;

        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("me", Factory());

        var slow = context.GetEndpoint(
                As2Dsl.Receive("/inbound").Host("127.0.0.1").Port(port).ConnectionFactory("me").Build())
            .CreateConsumer(new redb.Route.Processors.DelegateProcessor(async (e, ct) =>
            {
                entered.TrySetResult();
                await release.Task;
                finished = true;
            }));
        // A second route keeps the listener alive after the first is stopped.
        var other = context.GetEndpoint(
                As2Dsl.Receive("/other").Host("127.0.0.1").Port(port).ConnectionFactory("me").Build())
            .CreateConsumer(new redb.Route.Processors.DelegateProcessor(_ => { }));

        await slow.Start();
        await other.Start();

        var (contentType, transferEncoding, body) = BuildAs2Message("PO*4200~");
        using var client = new HttpClient();
        using var content = new ByteArrayContent(body);
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        content.Headers.TryAddWithoutValidation("Content-Transfer-Encoding", transferEncoding);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/inbound") { Content = content };
        request.Headers.TryAddWithoutValidation("AS2-From", "THEM");
        request.Headers.TryAddWithoutValidation("AS2-To", "US");
        request.Headers.TryAddWithoutValidation("Message-ID", "<drain-1@redb.route>");

        var call = client.SendAsync(request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var stop = slow.Stop();
        (await Task.WhenAny(stop, Task.Delay(500)) == stop)
            .Should().BeFalse("Stop must not return while a delivery of this route is still running");

        release.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(15));
        finished.Should().BeTrue();

        (await call).StatusCode.Should().Be(HttpStatusCode.OK);
        await other.Stop();
    }
}
