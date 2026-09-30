using System.Net;
using System.Text;
using FluentAssertions;
using redb.Route.As2;
using redb.Route.As2.Crypto;
using redb.Route.As2.Mdn;
using redb.Route.Core;
using As2Dsl = redb.Route.As2.Fluent.As2;
using static redb.Route.Tests.As2.As2TestKit;

namespace redb.Route.Tests.As2;

/// <summary>
/// What the receiver does for a request it cannot authenticate (REVIEW-2026-09-28 R1, R8): the partner is who
/// the agreement names, by its AS2 identifiers and its signature; until then its receipt URL is not called and
/// its document does not reach the route.
/// </summary>
public class As2ReceiveSecurityTests
{
    private static async Task<(RouteContext Context, List<string> Delivered, int Port)> StartReceiver(As2ConnectionFactory factory)
    {
        var port = FreePort();
        var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("recv", factory);
        var delivered = new List<string>();
        context.AddRoutes(r =>
            r.From(As2Dsl.Receive("/in").Host("127.0.0.1").Port(port).ConnectionFactory("recv"))
                .Process(e => { lock (delivered) delivered.Add(Encoding.UTF8.GetString((byte[])e.In.Body!)); }));
        await context.Start();
        return (context, delivered, port);
    }

    private static MdnParser.MdnResult ReadMdn(HttpResponseMessage response)
    {
        var cte = response.Content.Headers.TryGetValues("Content-Transfer-Encoding", out var v) ? string.Join(",", v) : null;
        var body = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        return MdnParser.Parse(response.Content.Headers.ContentType?.ToString(), cte, body, new As2CryptoEngine(), Cert);
    }

    [Fact]
    public async Task AsyncReceipt_ForAMessageSignedByAStranger_IsNotPosted()
    {
        using var trap = new As2Trap(FreePort());
        var (context, delivered, port) = await StartReceiver(Receiver(f =>
        {
            f.MdnMode = As2MdnMode.Async;
            f.AsyncMdnAllowedHosts = ["127.0.0.1"];
        }));
        await using var _ = context;

        var response = await PostAsync(port, "/in", BuildMessage("PO*FORGED~", Stranger, Cert),
            new Dictionary<string, string?> { ["Receipt-Delivery-Option"] = trap.Url() });

        (await trap.HitAsync(TimeSpan.FromSeconds(2))).Should().BeFalse(
            "the sender did not authenticate, so the address it named is not one we post to");
        delivered.Should().BeEmpty();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var mdn = ReadMdn(response);
        mdn.IsPositive.Should().BeFalse();
        mdn.Disposition.Should().EndWith("processed/error: authentication-failed");
    }

    [Fact]
    public async Task AsyncReceipt_InASyncPartnership_IsNotPosted_TheReceiptIsInTheResponse()
    {
        using var trap = new As2Trap(FreePort());
        var (context, delivered, port) = await StartReceiver(Receiver(f => f.AsyncMdnAllowedHosts = ["127.0.0.1"]));
        await using var _ = context;

        var response = await PostAsync(port, "/in", BuildMessage("PO*SYNC~", Cert, Cert),
            new Dictionary<string, string?> { ["Receipt-Delivery-Option"] = trap.Url() });

        (await trap.HitAsync(TimeSpan.FromSeconds(2))).Should().BeFalse("the agreement says the receipt is synchronous");
        delivered.Should().ContainSingle().Which.Should().Be("PO*SYNC~");
        ReadMdn(response).IsPositive.Should().BeTrue();
    }

    [Fact]
    public async Task AsyncReceipt_ToAHostTheAgreementDoesNotName_IsNotPosted()
    {
        using var trap = new As2Trap(FreePort());
        var (context, delivered, port) = await StartReceiver(Receiver(f =>
        {
            f.MdnMode = As2MdnMode.Async;
            f.AsyncMdnAllowedHosts = ["mdn.partner.example"];
        }));
        await using var _ = context;

        var response = await PostAsync(port, "/in", BuildMessage("PO*ELSEWHERE~", Cert, Cert),
            new Dictionary<string, string?> { ["Receipt-Delivery-Option"] = trap.Url() });

        (await trap.HitAsync(TimeSpan.FromSeconds(2))).Should().BeFalse("127.0.0.1 is not among asyncMdnAllowedHosts");
        delivered.Should().ContainSingle();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AsyncReceipt_OfAnAuthenticatedMessage_ToAnAllowedHost_IsPosted()
    {
        using var trap = new As2Trap(FreePort());
        var (context, delivered, port) = await StartReceiver(Receiver(f =>
        {
            f.MdnMode = As2MdnMode.Async;
            f.AsyncMdnAllowedHosts = ["127.0.0.1"];
        }));
        await using var _ = context;

        var response = await PostAsync(port, "/in", BuildMessage("PO*ASYNC~", Cert, Cert),
            new Dictionary<string, string?> { ["Receipt-Delivery-Option"] = trap.Url() });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await trap.HitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        delivered.Should().ContainSingle();
        trap.Requests.TryPeek(out var hit).Should().BeTrue();
        hit.Headers["AS2-From"].Should().Be("US");
        hit.Headers["AS2-To"].Should().Be("THEM");
    }

    [Theory]
    [InlineData("INTRUDER", "US")]
    [InlineData("THEM", "SOMEONE-ELSE")]
    [InlineData(null, "US")]
    [InlineData("THEM", null)]
    public async Task Message_WhoseAs2IdentifiersAreNotTheAgreements_IsRefused(string? from, string? to)
    {
        var (context, delivered, port) = await StartReceiver(Receiver());
        await using var _ = context;

        var response = await PostAsync(port, "/in", BuildMessage("PO*WHO~", Cert, Cert),
            new Dictionary<string, string?> { ["AS2-From"] = from, ["AS2-To"] = to });

        delivered.Should().BeEmpty("a document from, or to, a party the agreement does not name is not ours to process");
        var mdn = ReadMdn(response);
        mdn.IsPositive.Should().BeFalse();
        mdn.Disposition.Should().EndWith("processed/error: authentication-failed");
    }

    [Fact]
    public async Task Message_WithQuotedAs2Identifiers_IsTheAgreements()
    {
        // RFC 4130 §6.2: an identifier may be a quoted-string; the quotes are not part of it.
        var (context, delivered, port) = await StartReceiver(Receiver());
        await using var _ = context;

        var response = await PostAsync(port, "/in", BuildMessage("PO*QUOTED~", Cert, Cert),
            new Dictionary<string, string?> { ["AS2-From"] = "\"THEM\"", ["AS2-To"] = "\"US\"" });

        delivered.Should().ContainSingle();
        ReadMdn(response).IsPositive.Should().BeTrue();
    }

    [Fact]
    public async Task Message_WithAs2IdentifiersInAnotherCase_IsRefused()
    {
        // RFC 4130 §6.2: AS2-From and AS2-To are compared case-sensitively.
        var (context, delivered, port) = await StartReceiver(Receiver());
        await using var _ = context;

        await PostAsync(port, "/in", BuildMessage("PO*CASE~", Cert, Cert),
            new Dictionary<string, string?> { ["AS2-From"] = "them" });

        delivered.Should().BeEmpty();
    }
}
