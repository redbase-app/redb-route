using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;

using ConformanceModule;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using redb.Route.Abstractions;            // IExchange, IMessage
using redb.Route.As4;                     // As4Component, As4ConnectionFactory, As4Partner, As4Headers, As4Payload
using redb.Route.As4.Fluent;              // As4.Send / As4.Receive
using redb.Route.Components;              // AddIdempotentRepository
using redb.Route.Core;                    // RouteContext, Exchange, Message
using redb.Route.Processors;              // InMemoryIdempotentRepository

namespace ConformanceWorker;

/// <summary>
/// The module against a test driver of our own, on loopback: the message flow of the conformance testing service's
/// samples ("AS4 Basic Connectivity Two Way SUT C3"), plus the ebMS test service and an ebMS error. The driver is the
/// same connector with the parties swapped, so this proves the controller logic, not interoperability: that is what
/// the stands (Domibus, Holodeck) and the service itself are for.
/// </summary>
internal static class SelfTest
{
    private const int SutPort = 14190;
    private const int DriverPort = 14191;
    private const int ProxyPort = 14192;
    private const string SutRole = "http://www.edelivery.eu/as4/conformancetest/sut";
    private const string DriverRole = "http://www.edelivery.eu/as4/conformancetest/testdriver";
    private const string FailMarker = "selftest.fail";

    /// <summary>What the driver received: the AS4 headers and the payloads, copied out of the exchange.</summary>
    private sealed record Received(Dictionary<string, string> Headers, List<(string? Id, string Type, string Text, string? CharacterSet)> Payloads)
    {
        public string? Header(string name) => Headers.GetValueOrDefault(name);
        public string? Property(string name) => Headers.GetValueOrDefault(As4Headers.PropertyPrefix + name);
    }

    public static async Task<int> RunAsync(IServiceProvider sp)
    {
        using var sutKey = SelfSigned("CN=redb AS4 SUT (selftest)");
        using var driverKey = SelfSigned("CN=test driver (selftest)");

        // ── The module, exactly as the Tsak worker would configure it ──
        var settings = new ConformanceSettings
        {
            // The messages under test go to the driver through a proxy that can swallow its responses (a lost receipt).
            TestDriverUrl = $"http://127.0.0.1:{ProxyPort}/driver/msh",
            TestDriverControllerUrl = $"http://127.0.0.1:{DriverPort}/driver/controller",
            Port = SutPort,
            SutKey = sutKey,
            TestDriverCertificate = PublicOnly(driverKey),
            RetryCount = 1,
            RetryDelay = TimeSpan.FromSeconds(1),
            SendTimeout = 15000,
        };
        var sut = NewContext(sp, "as4-conformance");
        InitRoute.Configure(sut, settings);

        // ── The test driver ──
        var userMessages = Channel.CreateUnbounded<Received>();
        var commands = Channel.CreateUnbounded<Received>();
        var driver = NewContext(sp, "test-driver");
        ConfigureDriver(driver, settings, driverKey, PublicOnly(sutKey), userMessages, commands);

        await using var proxy = new LossyProxy(ProxyPort, $"http://127.0.0.1:{DriverPort}");
        await sut.Start();
        await driver.Start();

        var failures = 0;
        void Check(string what, bool ok, string? detail = null)
        {
            if (!ok) failures++;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(ok || detail is null ? "" : ": " + detail)}");
        }

        try
        {
            var conversation = Guid.NewGuid().ToString("D");
            var requestId = $"selftest-request-{Guid.NewGuid():N}@gitb.eu";
            var responseId = "AS4_RESPONSE_" + requestId;
            var sutUrl = $"http://127.0.0.1:{SutPort}{settings.SutPath}";
            var controllerUrl = $"http://127.0.0.1:{SutPort}{settings.ControllerPath}";

            // ── 1. Request leg: the driver sends a user message with two payloads; the SUT acknowledges and reports it ──
            Console.WriteLine("Two-Way, request leg (the SUT receives):");
            var request = NewMessage(Payloads());
            request.In.Headers[As4Headers.MessageId] = requestId;
            request.In.Headers[As4Headers.ConversationId] = conversation;
            await Send(driver, As4.Send(sutUrl).ConnectionFactory("driver").Partner(LegName(ConformanceLegs.TwoWay, "default")), request);
            Check("the SUT answers with a verified receipt", request.Out?.GetHeader<bool>(As4Headers.ReceiptValid) == true);

            var deliver = await Next(commands);
            Check("a Deliver command follows", deliver?.Header(As4Headers.Action) == "Deliver", deliver?.Header(As4Headers.Action));
            Check("Deliver names the message", deliver?.Property("messageId") == requestId, deliver?.Property("messageId"));
            Check("Deliver names the conversation", deliver?.Property("conversationId") == conversation);
            Check("Deliver names service and action",
                deliver?.Property("service") == ConformanceLegs.TwoWay && deliver?.Property("action") == "default");
            Check("Deliver names the parties and roles",
                deliver?.Property("fromPartyId") == settings.TestDriverPartyId && deliver?.Property("toPartyId") == settings.SutPartyId
                && deliver?.Property("fromPartyRole") == DriverRole && deliver?.Property("toPartyRole") == SutRole);
            CheckPayloads("Deliver", deliver, Check);

            // ── 2. Response leg: a Submit command asks the SUT to answer; the SUT sends, then reports the receipt ──
            Console.WriteLine("Two-Way, response leg (the SUT sends on a Submit command):");
            var submit = NewMessage(Payloads());
            Property(submit, "messageId", responseId);
            Property(submit, "refToMessageId", requestId);
            Property(submit, "conversationId", conversation);
            Property(submit, "fromPartyId", settings.SutPartyId);
            Property(submit, "fromPartyRole", SutRole);
            Property(submit, "toPartyId", settings.TestDriverPartyId);
            Property(submit, "toPartyRole", DriverRole);
            Property(submit, "service", ConformanceLegs.TwoWay);
            Property(submit, "action", "default");
            Property(submit, "originalSender", "C4");
            Property(submit, "finalRecipient", "C1");
            await Send(driver, As4.Send(controllerUrl).ConnectionFactory("driver-controller").Partner("Submit"), submit);
            Check("the Submit command is acknowledged", submit.Out?.GetHeader<bool>(As4Headers.ReceiptValid) == true);

            var response = await Next(userMessages);
            Check("the SUT sends the user message with the commanded id", response?.Header(As4Headers.MessageId) == responseId, response?.Header(As4Headers.MessageId));
            Check("it refers to the request", response?.Header(As4Headers.RefToMessageId) == requestId);
            Check("it keeps the conversation", response?.Header(As4Headers.ConversationId) == conversation);
            Check("it carries service and action of the leg",
                response?.Header(As4Headers.Service) == ConformanceLegs.TwoWay && response?.Header(As4Headers.Action) == "default");
            Check("it carries the four-corner properties of the command",
                response?.Property("originalSender") == "C4" && response?.Property("finalRecipient") == "C1");
            Check("it carries no command property", response?.Property("messageId") is null && response?.Property("service") is null);
            Check("its roles are the SUT's and the driver's",
                response?.Header(As4Headers.FromRole) == SutRole && response?.Header(As4Headers.ToRole) == DriverRole);
            CheckPayloads("the user message", response, Check);

            var notify = await Next(commands);
            Check("a Notify command follows", notify?.Header(As4Headers.Action) == "Notify", notify?.Header(As4Headers.Action));
            Check("Notify reports a receipt", notify?.Property("signalType") == "receipt", notify?.Property("signalType"));
            Check("Notify refers to the message the SUT sent", notify?.Property("refToMessageId") == responseId, notify?.Property("refToMessageId"));
            Check("Notify has no payload", notify?.Payloads.Count == 0);

            // ── 3. The ebMS test service: acknowledged, and reported to nobody ──
            Console.WriteLine("ebMS test service (ping):");
            var ping = NewMessage(Payloads());
            await Send(driver, As4.Send(sutUrl).ConnectionFactory("driver").Partner(LegName(ConformanceLegs.TestService, ConformanceLegs.TestAction)), ping);
            Check("the ping is acknowledged", ping.Out?.GetHeader<bool>(As4Headers.ReceiptValid) == true);
            Check("no Deliver follows a ping", await Next(commands, TimeSpan.FromSeconds(2)) is null);

            // ── 4. The driver refuses the message: the SUT reports the ebMS error ──
            Console.WriteLine("The test driver answers with an ebMS error:");
            var refusedId = $"selftest-refused-{Guid.NewGuid():N}@gitb.eu";
            var refused = NewMessage(Payloads());
            Property(refused, "messageId", refusedId);
            Property(refused, "service", ConformanceLegs.OneWay);
            Property(refused, "action", "default");
            Property(refused, FailMarker, "true");
            await Send(driver, As4.Send(controllerUrl).ConnectionFactory("driver-controller").Partner("Submit"), refused);
            var error = await Next(commands);
            Check("a Notify command follows", error?.Header(As4Headers.Action) == "Notify");
            Check("Notify reports an error", error?.Property("signalType") == "error", error?.Property("signalType"));
            Check("Notify carries the ebMS error code", error?.Property("errorCode")?.StartsWith("EBMS:", StringComparison.Ordinal) == true, error?.Property("errorCode"));
            Check("Notify refers to the refused message", error?.Property("refToMessageId") == refusedId, error?.Property("refToMessageId"));

            // ── 5. The receipt is lost once: the SUT resends the same message, the driver delivers it once ──
            Console.WriteLine("Retry profile, the first receipt is lost (AS4_CP_TA09, TA10):");
            var retriedId = $"selftest-retried-{Guid.NewGuid():N}@gitb.eu";
            proxy.Reset(swallow: 1);
            await Send(driver, As4.Send(controllerUrl).ConnectionFactory("driver-controller").Partner("Submit"), RetrySubmit(retriedId));
            var retried = await Next(userMessages);
            var retriedNotify = await Next(commands);
            Check("the driver gets the message", retried?.Header(As4Headers.MessageId) == retriedId, retried?.Header(As4Headers.MessageId));
            Check("the SUT sent it twice", proxy.Requests == 2, proxy.Requests.ToString());
            Check("the resend is the same message, byte for byte", proxy.DistinctBodies == 1, proxy.DistinctBodies.ToString());
            Check("the driver delivers it once", await Next(userMessages, TimeSpan.FromSeconds(2)) is null);
            Check("Notify reports the receipt of the resend",
                retriedNotify?.Property("signalType") == "receipt" && retriedNotify?.Property("refToMessageId") == retriedId,
                retriedNotify?.Property("signalType"));

            // ── 6. Every receipt is lost: after the redeliveries the SUT reports EBMS:0301 ──
            Console.WriteLine("Retry profile, no receipt ever arrives (AS4_CP_TA15):");
            var lostId = $"selftest-lost-{Guid.NewGuid():N}@gitb.eu";
            proxy.Reset(swallow: int.MaxValue);
            await Send(driver, As4.Send(controllerUrl).ConnectionFactory("driver-controller").Partner("Submit"), RetrySubmit(lostId));
            var lostNotify = await Next(commands);
            var lostDelivered = await Next(userMessages);
            Check("the driver did get the message once: only its receipts were lost",
                lostDelivered?.Header(As4Headers.MessageId) == lostId && await Next(userMessages, TimeSpan.FromSeconds(1)) is null);
            Check("the SUT sent it once and redelivered it RetryCount times", proxy.Requests == 1 + settings.RetryCount, proxy.Requests.ToString());
            Check("every transmission is the same message", proxy.DistinctBodies == 1, proxy.DistinctBodies.ToString());
            Check("Notify reports an error", lostNotify?.Property("signalType") == "error", lostNotify?.Property("signalType"));
            Check("the error is EBMS:0301 MissingReceipt", lostNotify?.Property("errorCode") == "EBMS:0301", lostNotify?.Property("errorCode"));
            Check("Notify refers to the message without a receipt", lostNotify?.Property("refToMessageId") == lostId, lostNotify?.Property("refToMessageId"));
            proxy.Reset(swallow: 0);

            // ── 7. PKI path: a signer that presents its certification path, and one that does not ──
            Console.WriteLine("PKI path (AS4_CP_TA47 to TA51):");
            var withPath = NewMessage(Payloads());
            await Send(driver, As4.Send(sutUrl).ConnectionFactory("driver").Partner(LegName(ConformanceLegs.OneWay, "bstPkiPath")), withPath);
            Check("a message signed with a path is acknowledged", withPath.Out?.GetHeader<bool>(As4Headers.ReceiptValid) == true);
            var pathDeliver = await Next(commands);
            Check("and reported with a Deliver", pathDeliver?.Property("action") == "bstPkiPath", pathDeliver?.Property("action"));

            var withoutPath = NewMessage(Payloads());
            string? refusal = null;
            try
            {
                await Send(driver, As4.Send(sutUrl).ConnectionFactory("driver").Partner(LegName(ConformanceLegs.OneWay, "bstPkiPathSutC3Fail")), withoutPath);
            }
            catch (As4ErrorSignalException e)
            {
                refusal = e.ErrorCode;
            }
            Check("a signer that presents no path is refused where one is required", refusal == "EBMS:0101", refusal);
            Check("and no Deliver follows", await Next(commands, TimeSpan.FromSeconds(2)) is null);

            var pathSubmitId = $"selftest-pkipath-{Guid.NewGuid():N}@gitb.eu";
            var pathSubmit = NewMessage(Payloads());
            Property(pathSubmit, "messageId", pathSubmitId);
            Property(pathSubmit, "service", ConformanceLegs.OneWay);
            Property(pathSubmit, "action", "bstPkiPath");
            await Send(driver, As4.Send(controllerUrl).ConnectionFactory("driver-controller").Partner("Submit"), pathSubmit);
            var pathMessage = await Next(userMessages);
            var pathNotify = await Next(commands);
            Check("on a Submit the SUT sends with its own path and the driver receives it", pathMessage?.Header(As4Headers.MessageId) == pathSubmitId, pathMessage?.Header(As4Headers.MessageId));
            Check("the driver's receipt, signed with a path, is verified", pathNotify?.Property("signalType") == "receipt", pathNotify?.Property("signalType"));
        }
        catch (Exception e)
        {
            failures++;
            Console.WriteLine($"  [FAIL] the scenario stopped: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            await driver.DisposeAsync();
            await sut.DisposeAsync();
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "Self-test passed." : $"Self-test FAILED: {failures} check(s).");
        return failures == 0 ? 0 : 1;
    }

    // ── The test driver ──────────────────────────────────────────────────────

    private static string LegName(string service, string action) =>
        "leg-" + ConformanceLegs.Default.ToList().FindIndex(l => l.Service == service && l.Action == action);

    private static void ConfigureDriver(RouteContext context, ConformanceSettings settings, X509Certificate2 driverKey,
        X509Certificate2 sutCertificate, Channel<Received> userMessages, Channel<Received> commands)
    {
        context.AddComponent(new As4Component());

        As4Partner Agreement(string name, string partyId, string service, string action) => new()
        {
            Name = name,
            PartyId = partyId,
            PartyIdType = ConformanceSettings.UnregisteredPartyIdType,
            OurRole = DriverRole,
            PartnerRole = SutRole,
            Service = service,
            Action = action,
            ReplyLeg = new As4Leg { Service = service, Action = action },
            RequireFourCornerProperties = false,
            PartnerSigningCertificates = { sutCertificate },
            PartnerEncryptionCertificate = sutCertificate,
        };

        var node = new As4ConnectionFactory
        {
            OurPartyId = settings.TestDriverPartyId,
            OurPartyIdType = ConformanceSettings.UnregisteredPartyIdType,
            ExternalHostName = "gitb.eu",
            SigningCertificate = driverKey,
            DecryptionCertificates = { driverKey },
        };
        foreach (var leg in ConformanceLegs.Default)
        {
            var agreement = Agreement(LegName(leg.Service, leg.Action), settings.SutPartyId, leg.Service, leg.Action);
            // The driver presents its certification path on the leg that expects one to be accepted, and its bare
            // certificate on the leg that expects the SUT to refuse it.
            if (leg.Action == "bstPkiPath") agreement.KeyReference = As4KeyReference.PkiPath;
            node.Partners.Add(agreement);
        }
        context.AddToRegistry("driver", node);

        var controller = new As4ConnectionFactory
        {
            OurPartyId = settings.TestDriverControllerPartyId,
            OurPartyIdType = ConformanceSettings.UnregisteredPartyIdType,
            ExternalHostName = "gitb.eu",
            SigningCertificate = driverKey,
            DecryptionCertificates = { driverKey },
        };
        foreach (var command in new[] { "Submit", "Deliver", "Notify" })
            controller.Partners.Add(Agreement(command, settings.ControllerPartyId, ConformanceLegs.CommandService, command));
        context.AddToRegistry("driver-controller", controller);

        context.AddIdempotentRepository("driver-in", new InMemoryIdempotentRepository(TimeSpan.FromHours(1)));
        context.AddIdempotentRepository("driver-controller-in", new InMemoryIdempotentRepository(TimeSpan.FromHours(1)));

        context.AddRoutes(r =>
        {
            r.From(As4.Receive("/driver/msh").Host("127.0.0.1").Port(DriverPort).ConnectionFactory("driver")
                    .IdempotentRepository("driver-in").PayloadList())
                .RouteId("driver-msh")
                .Process(e =>
                {
                    var received = Capture(e.In);
                    if (received.Property(FailMarker) is not null)
                        throw new InvalidOperationException("selftest: the driver refuses this message.");
                    userMessages.Writer.TryWrite(received);
                });

            r.From(As4.Receive("/driver/controller").Host("127.0.0.1").Port(DriverPort).ConnectionFactory("driver-controller")
                    .IdempotentRepository("driver-controller-in").PayloadList())
                .RouteId("driver-controller")
                .Process(e => commands.Writer.TryWrite(Capture(e.In)));
        });
    }

    private static Received Capture(IMessage message)
    {
        var headers = message.Headers
            .Where(h => h.Key.StartsWith(As4Headers.Prefix, StringComparison.Ordinal) && h.Value is not null)
            .ToDictionary(h => h.Key, h => h.Value!.ToString()!);
        var payloads = ((IReadOnlyList<As4Payload>)message.Body!)
            .Select(p => (p.ContentId, p.ContentType, new StreamReader(p.Content, leaveOpen: true).ReadToEnd(),
                p.Properties.TryGetValue("CharacterSet", out var set) ? set : null))
            .ToList();
        return new Received(headers, payloads);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    // The payload profile of the user guide (§5.3.3): an XML leading document and a non-XML one.
    private static List<As4Payload> Payloads() =>
    [
        new("xmlpayload@gitb", "application/xml", new MemoryStream(Encoding.UTF8.GetBytes("<hello>world</hello>")),
            new Dictionary<string, string> { ["CharacterSet"] = "UTF-8" }),
        new("custompayload@gitb", "application/octet-stream", new MemoryStream(Encoding.UTF8.GetBytes("custom payload")),
            new Dictionary<string, string> { ["CharacterSet"] = "UTF-8" }),
    ];

    private static void CheckPayloads(string of, Received? received, Action<string, bool, string?> check)
    {
        var payloads = received?.Payloads ?? [];
        check($"{of} carries both payloads in order, under their Content-IDs",
            payloads.Select(p => p.Id).SequenceEqual(["xmlpayload@gitb", "custompayload@gitb"]),
            string.Join(", ", payloads.Select(p => p.Id)));
        check($"{of} carries their content and media types",
            payloads.Select(p => (p.Type, p.Text)).SequenceEqual([("application/xml", "<hello>world</hello>"), ("application/octet-stream", "custom payload")]),
            null);
        check($"{of} carries the CharacterSet part property", payloads.Count == 2 && payloads.All(p => p.CharacterSet == "UTF-8"), null);
    }

    private static Exchange NewMessage(List<As4Payload> payloads) => new(new Message(payloads));

    private static Exchange RetrySubmit(string messageId)
    {
        var submit = NewMessage(Payloads());
        Property(submit, "messageId", messageId);
        Property(submit, "service", ConformanceLegs.OneWay);
        Property(submit, "action", "retry");
        return submit;
    }

    private static void Property(Exchange exchange, string name, string value) =>
        exchange.In.Headers[As4Headers.PropertyPrefix + name] = value;

    private static async Task Send(RouteContext context, string uri, IExchange exchange)
    {
        var producer = context.GetEndpoint(uri).CreateProducer();
        await producer.Start();
        try
        {
            await producer.Process(exchange);
        }
        finally
        {
            await producer.Stop();
        }
    }

    private static async Task<Received?> Next(Channel<Received> channel, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(20));
        try
        {
            return await channel.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static RouteContext NewContext(IServiceProvider sp, string id)
    {
        var context = new RouteContext(sp, contextId: id);
        context.AddService(typeof(ILoggerFactory), sp.GetRequiredService<ILoggerFactory>());
        return context;
    }

    // SELF-TEST ONLY: a self-signed RSA certificate. The conformance testing service hands out the real ones.
    private static X509Certificate2 SelfSigned(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx, "selftest"), "selftest");
    }

    private static X509Certificate2 PublicOnly(X509Certificate2 certificate) =>
        X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
}

/// <summary>
/// Stands between the SUT and the test driver, as the conformance tests "simulate the RMSH to not send receipts
/// (can be done by intercepting the receipts)": every request reaches the driver, and the first responses are
/// swallowed, answered to the SUT with an empty 202.
/// </summary>
internal sealed class LossyProxy : IAsyncDisposable
{
    private readonly System.Net.HttpListener _listener = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _target;
    private readonly Task _loop;
    private readonly HashSet<string> _bodies = [];
    private int _swallow;
    private int _requests;

    /// <param name="port">Port to listen on.</param>
    /// <param name="target">Scheme, host and port of the test driver the requests are forwarded to.</param>
    /// <param name="host">Address to listen on: loopback for the self-test, <c>+</c> inside a container.</param>
    public LossyProxy(int port, string target, string host = "127.0.0.1")
    {
        _target = target;
        _listener.Prefixes.Add($"http://{host}:{port}/");
        _listener.Start();
        _loop = Task.Run(ServeAsync);
    }

    /// <summary>Requests seen since the last <see cref="Reset"/>.</summary>
    public int Requests => Volatile.Read(ref _requests);

    /// <summary>Different request bodies among them: one when every transmission was the same bytes.</summary>
    public int DistinctBodies { get { lock (_bodies) return _bodies.Count; } }

    /// <summary>Forgets what was seen; the next <paramref name="swallow"/> responses of the driver are lost.</summary>
    public void Reset(int swallow)
    {
        Volatile.Write(ref _swallow, swallow);
        Volatile.Write(ref _requests, 0);
        lock (_bodies) _bodies.Clear();
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            System.Net.HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception e) when (e is System.Net.HttpListenerException or ObjectDisposedException or ArgumentException) { return; }

            try
            {
                using var body = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(body);
                var bytes = body.ToArray();
                Interlocked.Increment(ref _requests);
                lock (_bodies) _bodies.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));

                using var forward = new HttpRequestMessage(HttpMethod.Post, _target + context.Request.RawUrl) { Content = new ByteArrayContent(bytes) };
                forward.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
                using var answer = await _http.SendAsync(forward);
                var answerBytes = await answer.Content.ReadAsByteArrayAsync();

                var remaining = Volatile.Read(ref _swallow);
                if (remaining > 0)
                {
                    if (remaining != int.MaxValue) Volatile.Write(ref _swallow, remaining - 1);
                    context.Response.StatusCode = 202;
                }
                else
                {
                    context.Response.StatusCode = (int)answer.StatusCode;
                    context.Response.ContentType = answer.Content.Headers.ContentType?.ToString();
                    await context.Response.OutputStream.WriteAsync(answerBytes);
                }
                context.Response.Close();
            }
            catch (Exception e) when (e is System.Net.HttpListenerException or ObjectDisposedException or IOException or HttpRequestException)
            {
                // The listener is stopping, or the SUT gave up on this request.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        _listener.Close();
        _http.Dispose();
        await _loop;
    }
}
