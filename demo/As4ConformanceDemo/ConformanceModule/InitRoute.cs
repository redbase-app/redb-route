using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.DependencyInjection;

using redb.Route.Abstractions;            // IRouteContext, IExchange, IIdempotentRepository
using redb.Route.As4;                     // As4Component, As4ConnectionFactory, As4Partner, As4Headers, exceptions
using redb.Route.As4.Fluent;              // As4.Send / As4.Receive
using redb.Route.Components;              // AddIdempotentRepository
using redb.Route.Core;                    // RouteContext
using redb.Route;                         // Seda
using redb.Route.Processors;              // InMemoryIdempotentRepository
using redb.Route.RedbCore.Repositories;   // RedbIdempotentRepository

namespace ConformanceModule;

/// <summary>
/// Tsak module entry point: the System Under Test of the eDelivery AS4 conformance testing service.
/// <para>
/// The service drives an access point through its own "SUT Controller", which the owner of the access point writes
/// (user guide v2.90, §5.1.4). Here both are routes of one module:
/// </para>
/// <code>
///   test driver --Submit--&gt;  {ControllerPath}  --&gt; the user message it asks for --&gt; test driver
///                                                        | receipt, error or no receipt --Notify--&gt; test driver
///   test driver --user message--&gt;  {SutPath}  --&gt; receipt in the response
///                                                        | --Deliver--&gt; test driver   (not for the ebMS test service)
/// </code>
/// <para>
/// Two nodes, as the service uses two pairs of party ids: <c>sut</c> is the access point under test, <c>controller</c>
/// the command channel. A command is answered with its receipt first and acted on afterwards, from a <c>seda:</c>
/// queue: the ONEWAY_RETRY profile redelivers for minutes, far longer than a command's HTTP request should stay open.
/// </para>
/// </summary>
public static class InitRoute
{
    private const string SendQueue = "as4ct-send";
    private const string SendRetryQueue = "as4ct-send-retry";
    private const string DeliverQueue = "as4ct-deliver";

    /// <summary>Entry point the Tsak worker calls: the settings come from the route context.</summary>
    public static IRouteContext main(IRouteContext context) => Configure(context, ConformanceSettings.FromContext(context));

    /// <summary>Registers the two nodes and the routes for <paramref name="settings"/>.</summary>
    public static IRouteContext Configure(IRouteContext context, ConformanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);

        // The leg that requires a certificate policy needs one to require.
        var legs = ConformanceLegs.With(settings.ExtraLegs).Where(l => !l.WithPolicy || settings.PkiPathPolicy is not null).ToList();
        var byValues = legs.ToDictionary(l => (l.Service, l.Action));
        string PartnerName(ConformanceLeg leg) => "leg-" + legs.IndexOf(leg);
        var testDriver = X509CertificateLoader.LoadCertificate(settings.TestDriverCertificate.Export(X509ContentType.Cert));

        context.AddComponent(new As4Component());

        // ── The access point under test: one agreement per service and action of the tests ──
        var sut = new As4ConnectionFactory
        {
            OurPartyId = settings.SutPartyId,
            OurPartyIdType = ConformanceSettings.UnregisteredPartyIdType,
            ExternalHostName = settings.ExternalHostName,
            SigningCertificate = settings.SutKey,
            DecryptionCertificates = { settings.SutKey },
        };
        foreach (var authority in settings.SutChain)
            sut.SigningCertificateChain.Add(authority);
        foreach (var leg in legs)
        {
            var agreement = Agreement(PartnerName(leg), settings.TestDriverPartyId, leg.Service, leg.Action, testDriver, leg.KeyReference);
            if (leg.PkiPath)
            {
                // The test driver's signer is not pinned on these legs: it must present its certification path, and the
                // path must lead to a trust anchor (and assert the policy, where the leg asks for one).
                agreement.PartnerSigningCertificates.Clear();
                foreach (var anchor in settings.TestDriverTrustAnchors.Count > 0 ? settings.TestDriverTrustAnchors : [testDriver])
                    agreement.PartnerTrustAnchors.Add(anchor);
                if (leg.WithPolicy)
                    agreement.PartnerCertificatePolicies.Add(settings.PkiPathPolicy!);
            }
            sut.Partners.Add(agreement);
        }
        context.AddToRegistry("sut", sut);

        // ── The command channel: Submit comes in, Deliver and Notify go out ──
        var controller = new As4ConnectionFactory
        {
            OurPartyId = settings.ControllerPartyId,
            OurPartyIdType = ConformanceSettings.UnregisteredPartyIdType,
            ExternalHostName = settings.ExternalHostName,
            SigningCertificate = settings.SutKey,
            DecryptionCertificates = { settings.SutKey },
        };
        foreach (var command in new[] { "Submit", "Deliver", "Notify" })
            controller.Partners.Add(Agreement(command, settings.TestDriverControllerPartyId, ConformanceLegs.CommandService, command, testDriver));
        context.AddToRegistry("controller", controller);

        // Reception awareness: a resend gets its receipt again and reaches no route. Receive:DuplicateStore says where
        // the ids are remembered.
        context.AddIdempotentRepository("as4ct-sut", Duplicates(context, settings, "as4ct-sut"));
        context.AddIdempotentRepository("as4ct-controller", Duplicates(context, settings, "as4ct-controller"));

        var toTestDriver = As4.Send(settings.TestDriverUrl).ConnectionFactory("sut")
            .Partner("${header." + Commands.PartnerHeader + "}").Timeout(settings.SendTimeout).Build();
        var notify = As4.Send(settings.TestDriverControllerUrl).ConnectionFactory("controller").Partner("Notify").Timeout(settings.SendTimeout).Build();
        var deliver = As4.Send(settings.TestDriverControllerUrl).ConnectionFactory("controller").Partner("Deliver").Timeout(settings.SendTimeout).Build();

        var routes = (RouteContext)context;

        // ── Receiving ────────────────────────────────────────────────────────
        routes.AddRoutes(r =>
        {
            // A Submit command: answered with its receipt, then acted on from the queue of its profile.
            r.From(As4.Receive(settings.ControllerPath).Port(settings.Port).ConnectionFactory("controller")
                    .IdempotentRepository("as4ct-controller").PayloadList())
                .RouteId("as4ct-submit")
                .Log("Submit ${header.redbAs4.messageId}: send ${header.redbAs4.property.messageId} " +
                     "(${header.redbAs4.property.service} / ${header.redbAs4.property.action})")
                .Process(e => Commands.SubmitToUserMessage(e, byValues, PartnerName))
                .Choice()
                    .When(e => e.In.GetHeader<bool>(Commands.RetryHeader)).To(Seda.Send(SendRetryQueue))
                    .Otherwise().To(Seda.Send(SendQueue))
                .EndChoice();

            // A user message from the test driver. The receipt goes out with the response; the Deliver follows from
            // its queue. A message for the ebMS test service stops here: it reaches no business application
            // (profile §3.2.4, AS4_CP_TA17).
            r.From(As4.Receive(settings.SutPath).Port(settings.Port).ConnectionFactory("sut")
                    .IdempotentRepository("as4ct-sut").PayloadList())
                .RouteId("as4ct-receive")
                .Log("Received ${header.redbAs4.messageId} (${header.redbAs4.service} / ${header.redbAs4.action})")
                .Filter(e => e.In.GetHeader<string>(As4Headers.Service) != ConformanceLegs.TestService)
                    .Process(Commands.UserMessageToDeliver)
                    .To(Seda.Send(DeliverQueue))
                .End();
        });

        // ── Deliver ──────────────────────────────────────────────────────────
        routes.AddRoutes(r =>
        {
            r.From(Seda.Consume(DeliverQueue))
                .RouteId("as4ct-deliver")
                .To(deliver)
                .Log("Deliver sent for ${header.redbAs4.property.messageId}");
        });

        // ── Sending what a Submit asked for, then the Notify ─────────────────
        // Two builders, as an OnException belongs to the routes of its own builder: the retry profile redelivers a
        // missing receipt, every other profile reports it at once.
        routes.AddRoutes(r => SendRoute(r, SendQueue, "as4ct-send", toTestDriver, notify, redeliveries: 0, settings.RetryDelay));
        routes.AddRoutes(r => SendRoute(r, SendRetryQueue, "as4ct-send-retry", toTestDriver, notify, settings.RetryCount, settings.RetryDelay));

        return context;
    }

    /// <summary>
    /// Where one receive endpoint remembers the message ids it has taken. In memory the memory ends with the process: a
    /// message resent after a restart is delivered a second time. In the worker's redb it survives a restart and is
    /// shared by the nodes of a cluster; the entries of one endpoint are kept apart by <paramref name="scope"/>.
    /// </summary>
    private static IIdempotentRepository Duplicates(IRouteContext context, ConformanceSettings settings, string scope)
    {
        switch (settings.DuplicateStore.ToLowerInvariant())
        {
            case "memory":
                return new InMemoryIdempotentRepository(settings.DuplicateWindow);
            case "redb":
                var scopes = context.GetServiceProvider()?.GetService<IServiceScopeFactory>()
                    ?? throw new InvalidOperationException(
                        "Receive:DuplicateStore is 'redb', but the route context has no service provider with redb: " +
                        "run the module under a worker with a redb store, or set the store to 'memory'.");
                return new RedbIdempotentRepository(scopes, new RedbIdempotentOptions { ProcessorName = scope, Ttl = settings.DuplicateWindow });
            default:
                throw new InvalidOperationException(
                    $"Receive:DuplicateStore is '{settings.DuplicateStore}'; it is 'memory' or 'redb'.");
        }
    }

    private static void SendRoute(InlineRouteBuilder r, string queue, string routeId, string toTestDriver, string notify,
        int redeliveries, TimeSpan delay)
    {
        // No receipt: redelivered as the same message (same id, same bytes), then EBMS:0301 is reported (AS4_CP_TA09, TA15).
        r.OnException<As4ReceiptException>()
            .MaximumRedeliveries(redeliveries)
            .RedeliveryDelay(delay)
            .Handled()
            .Process(Commands.FailureToNotify)
            .To(notify);
        // The test driver's access point unreachable: the same policy.
        r.OnException<HttpRequestException>()
            .MaximumRedeliveries(redeliveries)
            .RedeliveryDelay(delay)
            .Handled()
            .Process(Commands.FailureToNotify)
            .To(notify);
        // An ebMS error is the test driver's answer: reported as it came, never redelivered.
        r.OnException<As4ErrorSignalException>()
            .Handled()
            .Process(Commands.FailureToNotify)
            .To(notify);

        r.From(Seda.Consume(queue))
            .RouteId(routeId)
            .To(toTestDriver)
            .Log("Sent ${header.redbAs4.messageId}, receipt ${header.redbAs4.receiptMessageId}")
            .Process(Commands.ReceiptToNotify)
            .To(notify);
    }

    /// <summary>
    /// One agreement with the test driver. The roles are the ones the tests use; the four-corner properties are not
    /// required, as the common profile does not ask for them: a message carries them when the test gives them. The
    /// reply leg has the values of the request leg, since a Two-Way test answers under the same service and action.
    /// </summary>
    private static As4Partner Agreement(string name, string partyId, string service, string action, X509Certificate2 certificate,
        As4KeyReference keyReference = As4KeyReference.BinarySecurityToken) => new()
    {
        Name = name,
        PartyId = partyId,
        PartyIdType = ConformanceSettings.UnregisteredPartyIdType,
        OurRole = "http://www.edelivery.eu/as4/conformancetest/sut",
        PartnerRole = "http://www.edelivery.eu/as4/conformancetest/testdriver",
        Service = service,
        Action = action,
        ReplyLeg = new As4Leg { Service = service, Action = action },
        KeyReference = keyReference,
        RequireFourCornerProperties = false,
        PartnerSigningCertificates = { certificate },
        PartnerEncryptionCertificate = certificate,
    };
}
