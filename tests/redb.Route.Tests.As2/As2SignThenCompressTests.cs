using System.Text;
using AwesomeAssertions;
using MimeKit;
using redb.Route.As2;
using redb.Route.As2.Crypto;
using redb.Route.As2.Mdn;
using redb.Route.Core;
using As2Dsl = redb.Route.As2.Fluent.As2;
using static redb.Route.Tests.As2.As2TestKit;

namespace redb.Route.Tests.As2;

/// <summary>
/// A message signed before it was compressed (RFC 5402 §3.3/3.6, OpenAS2's compress-after-signing), encrypted or not:
/// the receiver unwraps the compression and then the signature, verifies it, and computes the MIC over the signed part.
/// </summary>
public class As2SignThenCompressTests
{
    private const string Payload = "ISA*00*SIGN*THEN*COMPRESS~";

    private static async Task<(RouteContext Context, List<string> Delivered, int Port)> StartReceiver(bool encrypt)
    {
        var port = FreePort();
        var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("recv", Receiver(f => f.Encrypt = encrypt));
        var delivered = new List<string>();
        context.AddRoutes(r =>
            r.From(As2Dsl.Receive("/in").Host("127.0.0.1").Port(port).ConnectionFactory("recv"))
                .Process(e => { lock (delivered) delivered.Add(Encoding.UTF8.GetString((byte[])e.In.Body!)); }));
        await context.Start();
        return (context, delivered, port);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SignedThenCompressed_IsVerified_AndItsMicIsOverTheSignedPayload(bool encrypt)
    {
        var (context, delivered, port) = await StartReceiver(encrypt);
        await using var _ = context;

        var response = await PostAsync(port, "/in",
            BuildMessage(Payload, signer: Cert, recipient: encrypt ? Cert : null, compress: true, compressAfterSign: true));

        delivered.Should().ContainSingle().Which.Should().Be(Payload, "the payload, not the multipart/signed around it");
        var cte = response.Content.Headers.TryGetValues("Content-Transfer-Encoding", out var v) ? string.Join(",", v) : null;
        var mdn = MdnParser.Parse(response.Content.Headers.ContentType?.ToString(), cte,
            await response.Content.ReadAsByteArrayAsync(), new As2CryptoEngine(), Cert);
        mdn.IsPositive.Should().BeTrue();
        // What was signed is the payload part itself, prepared for signing as the sender prepared it.
        var signedPart = new MimePart("application", "edi-x12")
        {
            Content = new MimeContent(new MemoryStream(Encoding.UTF8.GetBytes(Payload))),
            ContentTransferEncoding = ContentEncoding.Binary,
        };
        mdn.ReceivedMic!.Value.Digest.Should().Be(new As2CryptoEngine().ComputeMic(signedPart, "sha-256", includeHeaders: true).Digest);
    }
}
