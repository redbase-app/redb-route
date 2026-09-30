using System.Security.Cryptography.X509Certificates;

using redb.Route.Abstractions;            // IRouteContext, IExchange
using redb.Route.As4;                     // As4Component, As4ConnectionFactory, As4Partner, As4Headers, exceptions
using redb.Route.As4.Fluent;              // As4.Send / As4.Receive
using redb.Route.Components;              // AddIdempotentRepository
using redb.Route.Core;                    // RouteContext
using redb.Route.File;                    // FileComponent, FileDsl, FileHeaders
using redb.Route.Processors;              // InMemoryIdempotentRepository

namespace As4Module;

/// <summary>
/// Tsak module entry point: an AS4 outbox and inbox.
/// <para>
/// The worker discovers it by convention (a public static class <c>InitRoute</c> with a public static
/// <c>main(IRouteContext)</c>) and calls it once when the module loads; the debug host (As4Worker/Program.cs) calls the
/// very same method.
/// </para>
/// <code>
///   {AS4_DEMO_DIR}/outbox/*   --file--&gt;  AS4 send to partner "demo-partner"  --receipt--&gt; moved to sent/
///                                                     | not delivered after 3 redeliveries --&gt; moved to failed/
///   AS4 receive :4090/as4/in  --&gt;  duplicates dropped  --&gt;  {AS4_DEMO_DIR}/inbox/{original file name}
/// </code>
/// <para>
/// The node sends to its own receiver (the partner is ourselves, with our certificate), so the demo runs with nothing
/// else installed. For a real partner, give <see cref="As4Partner"/> its party id, service, action and certificates, and
/// point the send URI at its access point.
/// </para>
/// </summary>
public static class InitRoute
{
    /// <summary>Port of the AS4 receiver (the AS4 connector's default).</summary>
    public const int Port = 4090;

    /// <summary>Password of the demo PFX the debug host generates in certs/node.pfx.</summary>
    public const string PfxPassword = "demo";

    public static IRouteContext main(IRouteContext context)
    {
        // The working folder: certs/node.pfx, outbox/, sent/, failed/, inbox/. Named explicitly: a path relative to the
        // current directory would depend on where the worker was started from.
        var dir = Environment.GetEnvironmentVariable("AS4_DEMO_DIR")
            ?? throw new InvalidOperationException("Set AS4_DEMO_DIR to the demo folder (certs/node.pfx, outbox/, inbox/).");
        string Sub(string name) => Path.Combine(dir, name);

        // Where the outbox sends: the partner's access point. The demo partner is ourselves, so by default our own
        // receiver; set AS4_DEMO_PARTNER_URL to send elsewhere (a closed port shows the redeliveries and failed/).
        var partnerUrl = Environment.GetEnvironmentVariable("AS4_DEMO_PARTNER_URL") is { Length: > 0 } url
            ? url
            : $"http://127.0.0.1:{Port}/as4/in";

        // Our key: signs messages and receipts, decrypts what the partner encrypts for us.
        var node = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(dir, "certs", "node.pfx"), PfxPassword);
        // The partner's public certificate: here our own, since the demo partner is ourselves.
        var partner = X509CertificateLoader.LoadCertificate(node.Export(X509ContentType.Cert));

        context.AddComponent(new As4Component());
        context.AddComponent(new FileComponent());

        // ── The node (our access point) and its one agreement ────────────────────
        context.AddToRegistry("node", new As4ConnectionFactory
        {
            OurPartyId = "urn:oasis:names:tc:ebcore:partyid-type:unregistered:redb-demo",
            ExternalHostName = "as4.redb-demo.example",     // host part of our message ids
            SigningCertificate = node,
            DecryptionCertificates = { node },
            Partners =
            {
                new As4Partner
                {
                    Name = "demo-partner",
                    PartyId = "urn:oasis:names:tc:ebcore:partyid-type:unregistered:redb-demo",
                    Service = "urn:redb:demo:services:documents",
                    Action = "Deliver",
                    PartnerSigningCertificates = { partner },   // verifies the partner's signatures
                    PartnerEncryptionCertificate = partner,     // we encrypt payloads for the partner
                },
            },
        });

        // Remembers received message ids: a resend is answered with a receipt and not delivered twice. In memory for the
        // demo; behind a load balancer, or to survive a restart, register the redb or SQL repository instead.
        context.AddIdempotentRepository("as4-in", new InMemoryIdempotentRepository(TimeSpan.FromDays(7)));

        ((RouteContext)context).AddRoutes(r =>
        {
            // ── No receipt, or an ebMS error: three redeliveries of the same message, then failed/ ──
            // A redelivery sends the same bytes (same message id, same signature), so the partner detects the duplicate.
            r.OnException<As4ReceiptException>()
                .MaximumRedeliveries(3)
                .RedeliveryDelay(TimeSpan.FromSeconds(2))
                .UseExponentialBackOff()
                .Log("AS4 not delivered: ${header.redbFile.Name} (${header.redbAs4.messageId})");
            // The partner's access point unreachable (refused, DNS, TLS): the same policy.
            r.OnException<HttpRequestException>()
                .MaximumRedeliveries(3)
                .RedeliveryDelay(TimeSpan.FromSeconds(2))
                .UseExponentialBackOff()
                .Log("AS4 not delivered, partner unreachable: ${header.redbFile.Name} (${header.redbAs4.messageId})");
            r.OnException<As4ErrorSignalException>()
                .Log("AS4 refused by the partner: ${header.redbFile.Name} (${header.redbAs4.messageId})");

            // ── Outbox: every file dropped into outbox/ is sent as one AS4 message ──────
            // Delivered: moved to sent/. Not delivered (the exchange failed after the redeliveries): moved to failed/.
            r.From(FileDsl.Read(Sub("outbox")).Delay(1000).MoveTo(Sub("sent")).MoveFailed(Sub("failed")))
                .RouteId("as4-outbox")
                // eDelivery four-corner properties: mandatory on every message.
                .SetHeader(As4Headers.OriginalSender, "urn:redb:demo:c1")
                .SetHeader(As4Headers.FinalRecipient, "urn:redb:demo:c4")
                // Any other redbAs4.property.* header travels as an eb:Property: the receiver restores the file name.
                .SetHeader(As4Headers.PropertyPrefix + "fileName", e => e.In.GetHeader<string>(FileHeaders.FileName))
                .Process(e => e.In.ContentType ??= ContentTypeOf(e.In.GetHeader<string>(FileHeaders.FileName)))
                .To(As4.Send(partnerUrl).ConnectionFactory("node").Partner("demo-partner").Timeout(30000))
                .Log("AS4 sent ${header.redbFile.Name}: message ${header.redbAs4.messageId}, receipt ${header.redbAs4.receiptMessageId}");

            // ── Inbox: our access point; one URL for every partner of the node ────────
            r.From(As4.Receive("/as4/in").Port(Port).ConnectionFactory("node").IdempotentRepository("as4-in"))
                .RouteId("as4-inbox")
                .Log("AS4 received from ${header.redbAs4.partner}: ${header.redbAs4.property.fileName} (${header.redbAs4.messageId})")
                .To(FileDsl.Write(Sub("inbox")).AutoCreate().FileName("${header.redbAs4.property.fileName}"));
        });

        return context;
    }

    // The payload's media type travels in its eb:PartInfo; a file carries none, so it is taken from the extension.
    private static string ContentTypeOf(string? fileName) => Path.GetExtension(fileName)?.ToLowerInvariant() switch
    {
        ".xml" => "application/xml",
        ".json" => "application/json",
        ".txt" => "text/plain",
        ".pdf" => "application/pdf",
        _ => "application/octet-stream",
    };
}
