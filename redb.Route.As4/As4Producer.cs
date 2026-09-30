using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.As4.Compression;
using redb.Route.Configuration;
using redb.Route.As4.Messaging;
using redb.Route.As4.Mime;
using redb.Route.As4.Security;
using redb.Route.As4.Signals;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.As4;

/// <summary>
/// AS4 sender (One-Way / Push, eDelivery AS4 1.16): builds the user message from the exchange and the agreement,
/// compresses, signs and encrypts the payload, posts it, and requires a signed non-repudiation receipt in the
/// response that names exactly what was signed. The outcome is on <c>exchange.Out</c> (InOut), as the AS2
/// producer puts its MDN there.
/// <para>
/// Failures are exceptions the route handles: <see cref="As4ErrorSignalException"/> when the partner refuses
/// the message, <see cref="As4ReceiptException"/> when no valid receipt proves receipt — the one to redeliver
/// on. The message id is written to <c>redbAs4.messageId</c> before the first attempt, so a redelivery of the
/// same exchange resends the same message.
/// </para>
/// </summary>
internal sealed class As4Producer : ConnectableProducer
{
    private readonly As4Endpoint _endpoint;
    private readonly As4EndpointOptions _options;
    private HttpClient? _http;
    private StreamCacheOptions _spool = new();

    public As4Producer(As4Endpoint endpoint, As4EndpointOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>The resolved node; set when the producer has started.</summary>
    internal As4Node? Node { get; private set; }

    /// <inheritdoc />
    protected override IEndpoint ProducerEndpoint => _endpoint;

    // The name reaches the started/stopped logs; a partner URL may carry userinfo no [Sensitive] reaches.
    /// <inheritdoc />
    protected override string ProducerName => $"as4:{EndpointUri.Sanitize(_endpoint.PartnerUrl)}";

    /// <inheritdoc />
    protected override Task ConnectAsync(CancellationToken ct)
    {
        if (_endpoint.PartnerUrl is null)
            throw new InvalidOperationException(
                $"AS4 send endpoint {_endpoint.Uri} has no partner address: use the host-style form as4s://host[:port]/path.");

        var node = As4Node.Resolve(_endpoint.Context, _options.ConnectionFactory!);
        _spool = As4Spool.Options(_endpoint.Context);

        if (_options.Partner is not { } partner)
            throw new InvalidOperationException($"AS4 send endpoint {_endpoint.Uri} requires 'partner': the Name of one of its node's partners.");

        // A static partner name is checked now; an expression can only be checked per message.
        if (!partner.IsDynamic)
        {
            var name = partner.Resolve(null!);   // static: the exchange is not read
            if (string.IsNullOrWhiteSpace(name) || !node.Partners.ContainsKey(name))
                throw new InvalidOperationException(
                    $"AS4 send endpoint {_endpoint.Uri}: partner '{name}' is not a partner of connection factory '{node.Name}'.");
        }

        // One client per producer, as the AS2 and HTTP producers keep.
        _http = new HttpClient(CreateHandler(node.Factory))
        {
            Timeout = _options.Timeout > 0 ? TimeSpan.FromMilliseconds(_options.Timeout) : Timeout.InfiniteTimeSpan,
        };

        Node = node;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Reads the partner's response within <see cref="As4EndpointOptions.MaxResponseBodySize"/>: refused on a declared
    /// length over it, else read as a stream that stops one byte past it. A receipt or an error is a few KB; anything
    /// larger (a proxy's error page, a broken partner) is not one, and is not buffered to find out.
    /// </summary>
    private async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, string messageId, CancellationToken ct)
    {
        var limit = _options.MaxResponseBodySize;
        if (response.Content.Headers.ContentLength is { } declared && declared > limit)
            throw new As4ReceiptException(As4ErrorCode.InvalidReceipt, messageId,
                $"the response declares {declared} bytes, over maxResponseBodySize {limit}.");

        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
                throw new As4ReceiptException(As4ErrorCode.InvalidReceipt, messageId,
                    $"the response is over maxResponseBodySize {limit} bytes.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Builds the request of <paramref name="messageId"/>: the body spooled as one payload (a large one to a temporary
    /// file), compressed per the agreement, signed, encrypted and written to a spool the HTTP content streams from.
    /// </summary>
    private async Task<As4Transmission> BuildAsync(IExchange exchange, As4Node node, As4Partner partner, string messageId, CancellationToken ct)
    {
        var host = node.Factory.ExternalHostName;
        var refToMessageId = Resolve(_options.RefToMessageId, exchange) ?? exchange.In.GetHeader<string>(As4Headers.RefToMessageId);
        var (service, serviceType, action) = BusinessValues(exchange, partner, isReply: refToMessageId is not null);
        var conversationId = Resolve(_options.ConversationId, exchange)
            ?? exchange.In.GetHeader<string>(As4Headers.ConversationId)
            ?? Guid.NewGuid().ToString("D");
        var properties = MessageProperties(exchange, partner);

        var contentId = "part-" + Guid.NewGuid().ToString("N") + "@" + host;
        var contentType = exchange.In.ContentType ?? "application/octet-stream";
        using var part = new SwaPart(contentId, contentType,
            await As4Spool.RunAsync(_spool, (output, token) => WritePayloadAsync(exchange.In.Body, output, token), ct).ConfigureAwait(false));
        var partProperties = partner.CompressPayloads
            ? As4Compression.Compress(part, _spool)
            : [new Property(As4Compression.MimeTypeProperty, part.ContentType, null)];

        var user = new UserMessage(
            new MessageInfo(DateTimeOffset.UtcNow, messageId, refToMessageId),
            new Party([new PartyId(node.Factory.OurPartyId!, node.Factory.OurPartyIdType)], partner.OurRole),
            new Party([new PartyId(partner.PartyId!, partner.PartyIdType)], partner.PartnerRole),
            new CollaborationInfo(
                partner.AgreementRef is null ? null : new AgreementRef(partner.AgreementRef, partner.AgreementRefType, partner.PModeId),
                new Service(service, serviceType), action, conversationId),
            properties,
            [new PartInfo(WsSecurityNames.CidUri(contentId), partProperties)],
            null);

        using var message = SwaMessage.Create(MessagingWriter.CreateEnvelope(user), _spool);
        message.Parts.Add(contentId, part);

        var signed = As4SecurityEngine.Sign(message, node.Factory.SigningCertificate!, partner.KeyReference, partner.TimestampTtl, DateTimeOffset.UtcNow);
        As4SecurityEngine.Encrypt(message, partner.PartnerEncryptionCertificate!, partner.KeyReference);
        string? wireType = null;
        var body = As4Spool.Run(_spool, output => wireType = message.WriteTo(output));
        return new As4Transmission(messageId, partner.Name!, exchange.In.Body, body, wireType!, signed);
    }

    /// <summary>
    /// The HTTP handler of a sender: the TLS versions the node allows (TLS 1.2 and 1.3 by default, as Holodeck, phase4
    /// and Domibus offer), and our client certificate for partners that require mutual TLS.
    /// </summary>
    internal static HttpClientHandler CreateHandler(As4ConnectionFactory node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var handler = new HttpClientHandler { SslProtocols = node.SslProtocols };
        if (node.ClientCertificate is { } clientCertificate)
            handler.ClientCertificates.Add(clientCertificate);
        return handler;
    }

    /// <inheritdoc />
    protected override Task DisconnectAsync(CancellationToken ct)
    {
        _http?.Dispose();
        _http = null;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        EnsureStarted();
        ArgumentNullException.ThrowIfNull(exchange);
        var node = Node!;
        var partner = ResolvePartner(exchange, node);
        var host = node.Factory.ExternalHostName;

        // The id is fixed on the exchange before the first attempt: a redelivery resends the same message.
        var messageId = exchange.In.GetHeader<string>(As4Headers.MessageId);
        if (string.IsNullOrWhiteSpace(messageId))
        {
            messageId = MessageIdFactory.New(host);
            exchange.In.Headers[As4Headers.MessageId] = messageId;
        }

        // Pinned keys are still checked for their validity period before anything is sent, on every attempt (Domibus
        // sender/receiver certificate validation on sending): a partner would refuse the message, and a receipt could not
        // be trusted either.
        var now = DateTimeOffset.UtcNow;
        if (CertificateValidity.Problem(node.Factory.SigningCertificate!, now, $"AS4 node '{node.Name}': our signing certificate", node.Factory.Revocation) is { } ours)
            throw new InvalidOperationException(ours);
        if (CertificateValidity.Problem(partner.PartnerEncryptionCertificate!, now, $"AS4 partner '{partner.Name}': the encryption certificate", node.Factory.Revocation) is { } theirs)
            throw new InvalidOperationException(theirs);

        // A redelivery on this exchange resends the message of the first attempt, byte for byte.
        if (exchange.Properties.TryGetValue(As4Transmission.Property, out var stored) && stored is As4Transmission prior
            && prior.IsFor(messageId, partner.Name!, exchange.In.Body))
        {
            Logger?.LogDebug("AS4 message {MessageId}: redelivery sends the transmission of the first attempt.", messageId);
        }
        else
        {
            prior = await BuildAsync(exchange, node, partner, messageId, ct).ConfigureAwait(false);
            exchange.Properties[As4Transmission.Property] = prior;
            ExchangeResources.ReleaseWithExchange(exchange, prior);
        }
        var transmission = prior;
        var sent = transmission.Signed;

        // The send span on the core contract (EnableTelemetry=false opens none); its context is written to the request,
        // replacing one already there: HttpClient writes none of its own when the header is present.
        using var activity = RouteTelemetryExtensions.StartTransportSpan(
            _endpoint.Context, $"{partner.Name} send", ActivityKind.Client, "messaging.system", "as4",
            _endpoint.Uri.NormalizedKey, destination: _endpoint.PartnerUrl, operation: "send");

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint.PartnerUrl) { Content = transmission.Content() };
        RouteTelemetryExtensions.InjectTraceContext(activity, request, static (r, name, value) =>
        {
            r.Headers.Remove(name);
            r.Headers.TryAddWithoutValidation(name, value);
        });
        _endpoint.RecordBytesOut(transmission.Body.Length);

        byte[] responseBody;
        string? responseType;
        SignalMessage receipt;
        try
        {
            using var response = await ReceiptWithin(_http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct), messageId, partner, ct).ConfigureAwait(false);
            responseBody = await ReceiptWithin(ReadBoundedAsync(response, messageId, ct), messageId, partner, ct).ConfigureAwait(false);
            responseType = response.Content.Headers.ContentType?.ToString();
            receipt = ReadReceipt(messageId, response, responseBody, responseType, partner, sent);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Unreachable, no receipt, an ebMS error or a receipt that does not verify: the send failed.
            activity.RecordFailure(e);
            throw;
        }

        var outMessage = new Message(responseBody) { ContentType = responseType };
        outMessage.Headers[As4Headers.MessageId] = messageId;
        outMessage.Headers[As4Headers.Partner] = partner.Name;
        outMessage.Headers[As4Headers.ReceiptValid] = true;
        outMessage.Headers[As4Headers.ReceiptMessageId] = receipt.MessageInfo.MessageId;
        exchange.Out = outMessage;
        exchange.Pattern = ExchangePattern.InOut;

        Logger?.LogDebug("AS4 message {MessageId} sent to partner {Partner}; receipt {ReceiptId} verified.",
            messageId, partner.Name, receipt.MessageInfo.MessageId);
    }

    // ── Receipt ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The verified receipt for <paramref name="messageId"/> in the partner's response, or the exception that
    /// says why there is none: the partner's own error signal, a response without a signal (missing receipt),
    /// or a receipt that does not verify or does not echo what we signed (invalid receipt).
    /// </summary>
    private SignalMessage ReadReceipt(string messageId, HttpResponseMessage response, byte[] body, string? contentType,
        As4Partner partner, IReadOnlyList<System.Xml.XmlElement> sent)
    {
        // A signal may come with any status (Holodeck answers an ebMS error with 200); only an empty body is "nothing".
        if (body.Length == 0 || string.IsNullOrWhiteSpace(contentType))
            throw new As4ReceiptException(As4ErrorCode.MissingReceipt, messageId,
                $"the partner answered HTTP {(int)response.StatusCode} without a signal.");

        SwaMessage answer;
        EbmsMessaging messaging;
        try
        {
            answer = SwaMessage.Read(contentType, body, _options.MaxEnvelopeCharacters);
            messaging = MessagingReader.Read(answer.Envelope);
        }
        catch (Exception e) when (e is As4ProcessingException or FormatException or System.Xml.XmlException or MimeKit.ParseException)
        {
            throw new As4ReceiptException(As4ErrorCode.InvalidReceipt, messageId,
                $"the partner's HTTP {(int)response.StatusCode} answer is not an ebMS signal.", e);
        }

        // Signals about our message; an error that names no message at all is about it too — a partner that could not
        // parse our header cannot quote its id.
        var signals = messaging.SignalMessages
            .Where(s => s.MessageInfo.RefToMessageId == messageId
                        || s.Errors.Any(e => e.RefToMessageInError == messageId)
                        || (s.MessageInfo.RefToMessageId is null && s.Errors.Count > 0 && s.Errors.All(e => e.RefToMessageInError is null)))
            .ToList();

        var failure = signals.SelectMany(s => s.Errors).FirstOrDefault(e => e.Severity == As4ErrorSeverity.Failure);
        if (failure is not null)
            throw new As4ErrorSignalException(messageId, failure.ErrorCode, failure.ShortDescription, failure.Description, failure.ErrorDetail);

        var receipt = signals.FirstOrDefault(s => s.IsReceipt)
            ?? throw new As4ReceiptException(As4ErrorCode.MissingReceipt, messageId,
                $"the partner's HTTP {(int)response.StatusCode} answer carries no receipt for it.");

        try
        {
            As4SecurityEngine.Verify(answer, partner.PartnerSigningCertificates.ToList(), partner.TimestampTolerance, DateTimeOffset.UtcNow,
                Node!.Factory.Revocation, partner.TimestampTimeToLive);
        }
        catch (CryptographicException e)
        {
            throw new As4ReceiptException(As4ErrorCode.InvalidReceipt, messageId, "the receipt's signature does not verify.", e);
        }

        if (!Receipts.ProvesReceiptOf(receipt, sent))
            throw new As4ReceiptException(As4ErrorCode.InvalidReceipt, messageId,
                "the receipt's non-repudiation information does not name what was signed.");

        return receipt;
    }

    // ── Building the message ─────────────────────────────────────────────────

    private As4Partner ResolvePartner(IExchange exchange, As4Node node)
    {
        var name = _options.Partner!.Value.Resolve(exchange);
        if (string.IsNullOrWhiteSpace(name) || !node.Partners.TryGetValue(name, out var partner))
            throw new InvalidOperationException(
                $"AS4 send endpoint {_endpoint.Uri}: partner '{name}' is not a partner of connection factory '{node.Name}'.");
        return partner;
    }

    /// <summary>
    /// Service and action: the agreement's request leg, or its reply leg when the message answers another one
    /// (Two-Way / Push-and-Push). An option or header may override them only where the agreement allows it.
    /// </summary>
    private (string Service, string? ServiceType, string Action) BusinessValues(IExchange exchange, As4Partner partner, bool isReply)
    {
        string service, action;
        string? serviceType;
        if (isReply)
        {
            var leg = partner.ReplyLeg
                ?? throw new InvalidOperationException(
                    $"AS4 partner '{partner.Name}': the message answers another one (refToMessageId), but the agreement has no ReplyLeg.");
            (service, serviceType, action) = (leg.Service!, leg.ServiceType, leg.Action!);
        }
        else
        {
            (service, serviceType, action) = (partner.Service!, partner.ServiceType, partner.Action!);
        }

        var serviceOverride = Resolve(_options.Service, exchange) ?? exchange.In.GetHeader<string>(As4Headers.Service);
        if (serviceOverride is not null && serviceOverride != service)
        {
            if (!partner.AllowOverrideService)
                throw new InvalidOperationException(
                    $"AS4 partner '{partner.Name}' does not allow overriding the service ('{service}' asked to be '{serviceOverride}').");
            service = serviceOverride;
        }

        var actionOverride = Resolve(_options.Action, exchange) ?? exchange.In.GetHeader<string>(As4Headers.Action);
        if (actionOverride is not null && actionOverride != action)
        {
            if (!partner.AllowOverrideAction)
                throw new InvalidOperationException(
                    $"AS4 partner '{partner.Name}' does not allow overriding the action ('{action}' asked to be '{actionOverride}').");
            action = actionOverride;
        }

        return (service, serviceType, action);
    }

    /// <summary>
    /// <c>eb:MessageProperties</c> from the exchange headers under <see cref="As4Headers.PropertyPrefix"/> — the only
    /// exchange headers that reach the partner. The eDelivery four-corner properties are required.
    /// </summary>
    private static IReadOnlyList<Property> MessageProperties(IExchange exchange, As4Partner partner)
    {
        var properties = exchange.In.Headers
            .Where(h => h.Key.StartsWith(As4Headers.PropertyPrefix, StringComparison.Ordinal) && h.Value is not null)
            .Select(h => new Property(h.Key[As4Headers.PropertyPrefix.Length..], h.Value!.ToString()!, null))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        foreach (var required in new[] { As4Headers.OriginalSender, As4Headers.FinalRecipient })
        {
            var name = required[As4Headers.PropertyPrefix.Length..];
            if (!properties.Any(p => p.Name == name && p.Value.Length > 0))
                throw new InvalidOperationException(
                    $"AS4 message to partner '{partner.Name}' has no '{name}' property: set the header '{required}' (eDelivery four-corner model).");
        }
        return properties;
    }

    /// <summary>
    /// Writes the body into the payload spool. A <see cref="Stream"/> is copied, not buffered, and not closed: the
    /// exchange owns it (the <c>ownsStream</c> rule of the stream connectors). A seekable one is read from its start,
    /// so a redelivery sends the same bytes (the SttProducer precedent); a forward-only one is read once — to redeliver
    /// it, the route caches it with <c>.StreamCaching()</c>.
    /// </summary>
    private static async Task WritePayloadAsync(object? body, Stream output, CancellationToken ct)
    {
        switch (body)
        {
            case null:
                return;
            case byte[] bytes:
                await output.WriteAsync(bytes, ct).ConfigureAwait(false);
                return;
            case string text:
                await output.WriteAsync(Encoding.UTF8.GetBytes(text), ct).ConfigureAwait(false);
                return;
            case Stream stream:
                if (stream.CanSeek) stream.Position = 0;
                await stream.CopyToAsync(output, ct).ConfigureAwait(false);
                return;
            default:
                // As the AS2 and HTTP producers do: any other body is sent as its UTF-8 text.
                await output.WriteAsync(Encoding.UTF8.GetBytes(body.ToString() ?? string.Empty), ct).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>
    /// Awaits one step of the exchange with the partner. A timeout is a missing receipt, not a cancellation: the
    /// engine's OnException and dead letter channel always rethrow <see cref="OperationCanceledException"/>, so an
    /// unconverted timeout could never be redelivered (the McpProducer precedent). A cancelled <paramref name="ct"/>
    /// stays a cancellation.
    /// </summary>
    private async Task<T> ReceiptWithin<T>(Task<T> step, string messageId, As4Partner partner, CancellationToken ct)
    {
        try
        {
            return await step.ConfigureAwait(false);
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new As4ReceiptException(As4ErrorCode.MissingReceipt, messageId,
                $"no response from partner '{partner.Name}' within {_options.Timeout} ms.", e);
        }
    }

    private static string? Resolve(DynamicValue<string>? value, IExchange exchange) =>
        value is { } v && v.Resolve(exchange) is { Length: > 0 } resolved ? resolved : null;
}
