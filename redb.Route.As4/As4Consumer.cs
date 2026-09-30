using System.Diagnostics;
using System.Security.Cryptography;
using System.Xml;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Configuration;
using redb.Route.As4.Compression;
using redb.Route.As4.Messaging;
using redb.Route.As4.Mime;
using redb.Route.As4.Security;
using redb.Route.As4.Signals;
using redb.Route.Core;
using redb.Route.Http;
using redb.Route.Telemetry;

namespace redb.Route.As4;

/// <summary>
/// AS4 receive server (One-Way / Push, eDelivery AS4 1.16) on the shared Kestrel host. For every request:
/// read the envelope (no DTD, bounded), match the agreement, decrypt, verify the signature, check the payload
/// references, decompress, hand one exchange to the route, and answer in the HTTP response — a signed
/// non-repudiation receipt when the unit of work succeeded (<c>EndedInFailure()</c> is false), an ebMS error
/// otherwise. The receipt therefore goes out after the route's transaction has committed.
/// <para>
/// What the partner reads never carries the text of one of our exceptions (BR-4): each ebMS code has a fixed
/// text and the answer quotes a reference; the detail goes to the log. Decryption and signature failures all
/// get the same answer of their code, so varying the input teaches the sender nothing.
/// </para>
/// </summary>
internal sealed class As4Consumer : IConsumer
{
    private readonly As4Endpoint _endpoint;
    private readonly IProcessor _processor;
    private readonly As4EndpointOptions _options;
    private readonly InflightDrainGuard _drain = new();
    private RouteRegistration? _registration;
    private As4Node? _node;
    private IIdempotentRepository? _duplicates;
    private StreamCacheOptions _spool = new();
    private ILogger? _logger;

    public As4Consumer(As4Endpoint endpoint, IProcessor processor, As4EndpointOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public IEndpoint Endpoint => _endpoint;

    /// <summary>The engine pipeline received messages are handed to.</summary>
    internal IProcessor Processor => _processor;

    private SharedHttpServerManager Server => ((As4Component)_endpoint.Component).Server;

    private string Path => _endpoint.Uri.Path;

    /// <inheritdoc />
    public async Task Start(CancellationToken ct = default)
    {
        if (_endpoint.PartnerUrl is not null)
            throw new InvalidOperationException(
                $"AS4 receive endpoint {_endpoint.Uri} uses the host-style send form; a receive endpoint is as4:/path?host=...&port=....");

        _logger ??= _endpoint.Logger;
        _node = As4Node.Resolve(_endpoint.Context, _options.ConnectionFactory!);

        // eDelivery AS4 1.16 mandates duplicate detection; the store is named, as the S3 consumer names it.
        if (string.IsNullOrWhiteSpace(_options.IdempotentRepository))
            throw new InvalidOperationException(
                $"AS4 receive endpoint {_endpoint.Uri} requires 'idempotentRepository': the name of an IIdempotentRepository registered with " +
                "context.AddIdempotentRepository(name, repository). It remembers received message ids so a resend is not delivered twice.");
        var context = _endpoint.Context
            ?? throw new InvalidOperationException($"AS4 receive endpoint {_endpoint.Uri} has no route context to resolve its idempotent repository from.");
        _duplicates = context.GetIdempotentRepositoryProvider().Get(_options.IdempotentRepository);
        _spool = As4Spool.Options(context);

        // TLS material, as the SOAP receiver resolves it: the URI wins where it says something, the node fills the rest
        // (a password in a URI travels into logs and the dashboard).
        var certPath = _options.SslCertPath ?? _node.Factory.SslCertPath;
        var certPassword = _options.SslCertPassword ?? _node.Factory.SslCertPassword;
        var certMode = _options.ClientCertificateMode != As4ClientCertificateMode.NoCertificate
            ? _options.ClientCertificateMode
            : _node.Factory.ClientCertificateMode;
        var thumbprints = _options.AllowedClientThumbprints ?? _node.Factory.AllowedClientThumbprints;

        // Fail here rather than inside Kestrel, as the SOAP receiver does: without a certificate the listener would fall
        // back to a host default (present on a developer's machine, absent in production), and a partner would meet a
        // certificate nobody agreed on.
        if (_options.UseTls && string.IsNullOrWhiteSpace(certPath))
            throw new InvalidOperationException(
                $"AS4 receive endpoint on {_options.Host}:{_options.Port}{Path} is set to serve TLS but has no certificate. " +
                "Set sslCertPath on the endpoint URI, or SslCertPath on the connection factory it names.");
        if (certMode != As4ClientCertificateMode.NoCertificate && !_options.UseTls)
            throw new InvalidOperationException(
                $"AS4 receive endpoint on {_options.Host}:{_options.Port}{Path} asks for client certificates without TLS: a client " +
                "certificate is presented in the TLS handshake. Use the as4s scheme, or drop clientCertificateMode.");

        _drain.Start(ct);

        _registration = Server.RegisterRoute(_options.Host, _options.Port, Path, "POST", HandleRequest,
            _options.UseTls, certPath, certPassword,
            corsOptions: null,
            maxRequestBodySize: _options.MaxRequestBodySize,
            clientCertificateMode: MapClientCertificateMode(certMode),
            clientCertificateValidation: ThumbprintValidator(thumbprints, _node.Factory.Revocation),
            concurrencyLimit: ConcurrencyLimitOptions.FromEndpoint(
                _options.MaxConcurrentRequests, _options.RequestQueueLimit,
                _options.RejectStatusCode, _options.RetryAfterSeconds,
                onRejected: _endpoint.RecordRejected));
        await Server.EnsureStarted(_options.Host, _options.Port, ct).ConfigureAwait(false);

        _logger?.LogInformation("AS4 receiver started: {Host}:{Port}{Path}, node {Node}", _options.Host, _options.Port, Path, _node.Name);
    }

    /// <inheritdoc />
    public async Task Stop(CancellationToken ct = default)
    {
        if (_registration is null) return;
        // Unregister, drain what is already inside, then release the listener — the order of the HTTP family.
        Server.UnregisterRoute(_registration);
        _registration = null;
        await _drain.DrainAsync(ct, _logger, $"as4://{_options.Host}:{_options.Port}{Path}").ConfigureAwait(false);
        await Server.StopIfEmpty(_options.Host, _options.Port, ct).ConfigureAwait(false);
        _logger?.LogInformation("AS4 receiver stopped: {Host}:{Port}{Path}", _options.Host, _options.Port, Path);
    }

    private async Task HandleRequest(HttpContext http)
    {
        // Counted for the drain: a message already inside the pipeline must be answered before Stop returns.
        _drain.Increment();
        try
        {
            await HandleRequestCore(http).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // What the receiver did not foresee is still an ebMS answer (BR-4): logged with its reference and counted, the
            // partner gets EBMS:0004 and the reference, never a bare 500 it cannot tell from a network failure.
            _logger?.LogError(e, "AS4 receiver {Path}: unexpected failure (ref {Reference}).", Path, http.TraceIdentifier);
            _endpoint.RecordError(e);
            if (!http.Response.HasStarted)
                await WriteSignal(http, Responses.Error(As4ErrorCode.Other, null, _node!.Factory.ExternalHostName, http.TraceIdentifier)).ConfigureAwait(false);
        }
        finally
        {
            _drain.Decrement();
        }
    }

    private async Task HandleRequestCore(HttpContext http)
    {
        var node = _node!;
        var host = node.Factory.ExternalHostName;
        var reference = http.TraceIdentifier;
        // The Consumer span on the core contract: the host's request span when the application traces ASP.NET Core, else the
        // sender's traceparent, else a root; the sender's baggage is put back; EnableTelemetry=false opens nothing.
        using var span = RouteTelemetryExtensions.StartConsumerSpan(
            _endpoint.Context, $"{Path} receive", ActivityKind.Consumer, "messaging.system", "as4", _endpoint.Uri.NormalizedKey,
            http.Request.Headers, static (headers, name) => headers.TryGetValue(name, out var value) ? value.ToString() : null,
            InboundParent.HostRequest, destination: Path, operation: "receive");
        var activity = span.Activity;

        // The request is spooled, not buffered: past the threshold it goes to a temporary file (Kestrel bounds it by
        // maxRequestBodySize), and MimeKit reads the parts from there without another copy.
        var body = new StreamCache(_spool);

        // ── Not ebMS at all: a SOAP fault ──
        SwaMessage message;
        try
        {
            await body.CacheFromSourceAsync(http.Request.Body, http.RequestAborted).ConfigureAwait(false);
            body.CompleteWriting();
            message = SwaMessage.Read(http.Request.ContentType ?? string.Empty, body, _options.MaxEnvelopeCharacters, _spool);
        }
        catch (XmlException e)
        {
            await body.DisposeAsync().ConfigureAwait(false);
            // A DTD (forbidden in SOAP, SOAP 1.2 Part 1 §5) or broken XML: the text describes the caller's own bytes.
            _endpoint.RecordError(e);
            activity.RecordFailure(e);
            await Write(http, Responses.SoapFault(mustUnderstand: false, $"Malformed SOAP envelope: {e.Message}")).ConfigureAwait(false);
            return;
        }
        catch (Exception e) when (e is FormatException or MimeKit.ParseException or ArgumentException)
        {
            await body.DisposeAsync().ConfigureAwait(false);
            _endpoint.RecordError(e);
            activity.RecordFailure(e);
            await WriteSignal(http, Responses.Error(As4ErrorCode.MimeInconsistency, null, host, reference)).ConfigureAwait(false);
            return;
        }
        catch
        {
            await body.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        using var owned = message;   // parts the route did not take, and the spooled request

        if (EnvelopeProblem(message.Envelope) is { } notSoap)
        {
            _endpoint.RecordError();
            activity?.SetStatus(ActivityStatusCode.Error);
            await Write(http, Responses.SoapFault(mustUnderstand: false, notSoap)).ConfigureAwait(false);
            return;
        }

        if (UnderstoodHeaders(message.Envelope) is { } unknownBlock)
        {
            _endpoint.RecordError();
            activity?.SetStatus(ActivityStatusCode.Error);
            await Write(http, Responses.SoapFault(mustUnderstand: true, $"Header block {unknownBlock} is not understood.")).ConfigureAwait(false);
            return;
        }

        // ── ebMS processing up to the route: every failure is an ebMS error ──
        string? messageId = null;
        As4Partner? partner = null;
        Exchange exchange;
        SignatureVerification verification;
        try
        {
            var messaging = MessagingReader.Read(message.Envelope);
            if (messaging.SignalMessages.FirstOrDefault(s => s.PullRequestMpc is not null) is { } pull)
            {
                // No pull process here (pull is out of scope): EBMS:0010, as Domibus answers a pull request for an mpc
                // without one (PullRequestLegConfigurationExtractor).
                messageId = pull.MessageInfo.MessageId;
                throw new As4ProcessingException(As4ErrorCode.ProcessingModeMismatch,
                    $"a pull request for mpc '{pull.PullRequestMpc}': this receiver supports push only.");
            }
            if (messaging.UserMessages.Count == 0)
            {
                // A signal on the message endpoint: eDelivery 1.16 forbids asynchronous signals, so nothing waits for it.
                _logger?.LogWarning("AS4 receiver {Path}: a signal message arrived and no exchange waits for it; ignored.", Path);
                http.Response.StatusCode = StatusCodes.Status202Accepted;
                return;
            }

            var user = messaging.UserMessages[0];
            messageId = user.MessageInfo.MessageId;
            (partner, _) = node.MatchInbound(user);
            verification = Secure(message, node, partner);
            RequireFourCornerProperties(user);
            exchange = CreateExchange(http, user, message, partner, verification);
        }
        catch (As4ProcessingException e)
        {
            _logger?.LogWarning("AS4 receiver {Path}: message {MessageId} refused with {Code} (ref {Reference}): {Detail}",
                Path, messageId, e.Code.Code, reference, e.Detail);
            _endpoint.RecordError(e);
            activity.RecordFailure(e);
            await WriteSignal(http, Responses.Error(e.Code, messageId, host, reference,
                signer: partner is null ? null : node.Factory.SigningCertificate,
                keyReference: partner?.KeyReference ?? As4KeyReference.BinarySecurityToken)).ConfigureAwait(false);
            return;
        }

        try
        {
            // ── Duplicate detection: the two-phase claim of the S3 consumer (Add, then Confirm or Remove) ──
            // Claimed after the security checks, so a forged message cannot burn the id of a real one. The claim is a short
            // state write taken uncancelled, as Confirm and Remove are: cut by the sender's disconnect it could be written
            // and reported failed, leaving the id marked for a message the route never saw. A disconnect after it reaches
            // the route, whose cancellation releases the claim.
            bool claimed;
            try
            {
                claimed = await _duplicates!.Add(messageId!, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger?.LogError(e, "AS4 receiver {Path}: duplicate check for message {MessageId} failed (ref {Reference}).", Path, messageId, reference);
                _endpoint.RecordError(e);
                activity.RecordFailure(e);
                await WriteSignal(http, Responses.Error(As4ErrorCode.Other, messageId, host, reference,
                    signer: node.Factory.SigningCertificate, keyReference: partner!.KeyReference)).ConfigureAwait(false);
                return;
            }

            if (!claimed)
            {
                // Received before: answer with a receipt again — built from the same bytes, so the same non-repudiation
                // information — and do not deliver it twice. The sender stops resending once it has a receipt.
                _logger?.LogInformation("AS4 receiver {Path}: message {MessageId} is a duplicate; receipt sent again, not delivered.", Path, messageId);
                await WriteSignal(http, Responses.Receipt(messageId!, verification.References, host,
                    node.Factory.SigningCertificate!, partner!.KeyReference, partner.TimestampTtl)).ConfigureAwait(false);
                return;
            }

            // ── The route; the answer follows its unit of work ──
            try { await _processor.Process(exchange, http.RequestAborted).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                // Aborted mid-route (the sender went away, or the host stopped after the drain timeout): the message
                // was not processed, so its resend must be delivered — released on any failure, as the S3 consumer does.
                await ReleaseClaimAsync(messageId!).ConfigureAwait(false);
                throw;
            }
            catch (Exception e) { exchange.Exception ??= e; }

            if (exchange.EndedInFailure())
            {
                // No RecordError: the core StatisticsProcessor saw the same exchange.Exception. The span shows it red.
                if (exchange.Exception is { } routeFailure) activity.RecordFailure(routeFailure);
                await ReleaseClaimAsync(messageId!).ConfigureAwait(false);   // the sender resends; that is not a duplicate
                var callerText = (exchange.Exception as MalformedRequestException)?.Message;
                var code = callerText is null ? As4ErrorCode.Other : As4ErrorCode.ValueInconsistent;
                _logger?.LogWarning(exchange.Exception, "AS4 receiver {Path}: route failed for message {MessageId}; answered {Code} (ref {Reference}).",
                    Path, messageId, code.Code, exchange.ExchangeId);
                await WriteSignal(http, Responses.Error(code, messageId, host, exchange.ExchangeId, callerText,
                    node.Factory.SigningCertificate, partner!.KeyReference)).ConfigureAwait(false);
                return;
            }

            await ConfirmClaimAsync(messageId!).ConfigureAwait(false);
            await WriteSignal(http, Responses.Receipt(messageId!, verification.References, host,
                node.Factory.SigningCertificate!, partner!.KeyReference, partner.TimestampTtl)).ConfigureAwait(false);
        }
        finally
        {
            await exchange.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Confirms the claim of a processed message. The work is done by now, so a failure to confirm is logged and
    /// the receipt still goes out; the claim itself (from Add) already keeps a resend from being delivered.
    /// </summary>
    private async Task ConfirmClaimAsync(string messageId)
    {
        try
        {
            await _duplicates!.Confirm(messageId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger?.LogWarning(e, "AS4 receiver {Path}: confirming duplicate claim for message {MessageId} failed.", Path, messageId);
        }
    }

    /// <summary>Releases the claim of a message the route did not process, so its resend is delivered (as the S3 consumer does).</summary>
    private async Task ReleaseClaimAsync(string messageId)
    {
        try
        {
            await _duplicates!.Remove(messageId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger?.LogWarning(e, "AS4 receiver {Path}: releasing duplicate claim for message {MessageId} failed — it stays marked until manual cleanup.", Path, messageId);
        }
    }

    /// <summary>
    /// Decrypts, verifies and checks the policy of the agreement: eDelivery mandates a signed message with
    /// encrypted payloads, so a message without either is refused before any other check. Throws the ebMS code
    /// for each failure; decryption and signature failures carry the detail only for our log.
    /// </summary>
    private SignatureVerification Secure(SwaMessage message, As4Node node, As4Partner partner)
    {
        SoapParts parts;
        try
        {
            parts = SoapParts.Of(message.Envelope);
        }
        catch (CryptographicException e)
        {
            // Two wsse:Security headers for one role (WS-Security 1.1 §6.1): a policy failure, as Domibus reports WSS4J's.
            throw new As4ProcessingException(As4ErrorCode.PolicyNoncompliance, e.Message, e);
        }
        var security = parts.Security;
        var signed = security is not null && security.ChildNodes.OfType<XmlElement>().Any(e => e.LocalName == "Signature" && e.NamespaceURI == WsSecurityNames.Ds);
        var encrypted = security is not null && security.ChildNodes.OfType<XmlElement>().Any(e => e.LocalName == "EncryptedKey" && e.NamespaceURI == WsSecurityNames.Xenc);
        if (!signed)
            throw new As4ProcessingException(As4ErrorCode.PolicyNoncompliance, "the message is not signed.");
        if (message.Parts.Count > 0 && !encrypted)
            throw new As4ProcessingException(As4ErrorCode.PolicyNoncompliance, "the payloads are not encrypted.");

        try
        {
            As4SecurityEngine.Decrypt(message, node.Factory.DecryptionCertificates);
        }
        catch (CryptographicException e)
        {
            throw new As4ProcessingException(As4ErrorCode.FailedDecryption, e.Message, e);
        }

        try
        {
            return As4SecurityEngine.Verify(message, partner.PartnerSigningCertificates.ToList(), partner.TimestampTolerance, DateTimeOffset.UtcNow,
                node.Factory.Revocation, partner.TimestampTimeToLive);
        }
        catch (CryptographicException e)
        {
            throw new As4ProcessingException(As4ErrorCode.FailedAuthentication, e.Message, e);
        }
    }

    private Exchange CreateExchange(HttpContext http, UserMessage user, SwaMessage message, As4Partner partner, SignatureVerification verification)
    {
        // Every payload is decompressed before any is handed on: a failure leaves them all with the message.
        var matched = new List<(PartInfo Info, SwaPart Part)>();
        foreach (var (info, part) in PayloadConsistency.Match(user, message))
        {
            if (part is null)
                throw new As4ProcessingException(As4ErrorCode.FeatureNotSupported, "a payload in the SOAP body is not supported; AS4 carries payloads as attachments.");
            As4Compression.Decompress(part, info, MaxDecompressedBytes, message.Spool);
            matched.Add((info, part));
        }

        // One payload is the body itself — byte[], or with streamBody the spooled stream, which the exchange closes
        // (Exchange.DisposeAsync), as the File and S3 consumers hand theirs. Several are As4Payload streams the
        // exchange releases (ExchangeResources.ReleaseWithExchange).
        object? body;
        List<As4Payload>? payloads = null;
        switch (matched.Count)
        {
            case 0:
                body = null;
                break;
            case 1:
                body = _options.StreamBody ? matched[0].Part.Detach() : matched[0].Part.ToArray();
                break;
            default:
                payloads = matched.Select(m => new As4Payload(m.Part.ContentId, m.Part.ContentType, m.Part.Detach(),
                    m.Info.Properties.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal))).ToList();
                body = payloads;
                break;
        }

        var messageOut = new Message(body) { ContentType = matched.Count == 1 ? matched[0].Part.ContentType : null };
        Headers(messageOut, user, partner, verification);
        if (http.Connection.RemoteIpAddress is { } remote)
            messageOut.Headers[As4Headers.RemoteAddress] = remote.ToString();

        // mTLS: what the handshake proved about the caller, as the SOAP receiver puts it on the exchange.
        if (http.Connection.ClientCertificate is { } clientCert)
        {
            messageOut.Headers[As4Headers.ClientCertThumbprint] = clientCert.Thumbprint;
            messageOut.Headers[As4Headers.ClientCertSubject] = clientCert.Subject;
            messageOut.Headers[As4Headers.ClientCertNotAfter] = clientCert.NotAfter.ToUniversalTime().ToString("O");
        }

        var exchange = Exchange.Create(messageOut, _endpoint.ScopeFactory);
        foreach (var payload in payloads ?? [])
            ExchangeResources.ReleaseWithExchange(exchange, payload.Content);
        exchange.Pattern = ExchangePattern.InOut;
        ExchangePrincipal.Set(exchange, Principal(http, partner, verification));
        return exchange;
    }

    /// <summary>
    /// Who sent the message (docs/as4/09 No. 10, as camel-cxf with WSS4J takes the principal from the signature): the
    /// envelope signer first — its subject, thumbprint and the agreement it authenticated — then the TLS client
    /// certificate when the partner presented one, then whatever the host's principal resolver adds.
    /// </summary>
    private static System.Security.Claims.ClaimsPrincipal Principal(HttpContext http, As4Partner partner, SignatureVerification verification)
    {
        var signer = verification.Signer;
        var identities = new List<System.Security.Claims.ClaimsIdentity>
        {
            new([
                new(System.Security.Claims.ClaimTypes.NameIdentifier, partner.Name!),
                new(System.Security.Claims.ClaimTypes.Name, signer.Subject),
                new(System.Security.Claims.ClaimTypes.Thumbprint, signer.Thumbprint),
            ], As4AuthenticationTypes.Signature),
        };
        if (http.Connection.ClientCertificate is { } clientCert)
            identities.Add(new([
                new(System.Security.Claims.ClaimTypes.Name, clientCert.Subject),
                new(System.Security.Claims.ClaimTypes.Thumbprint, clientCert.Thumbprint),
            ], As4AuthenticationTypes.ClientCertificate));
        if (SharedHttpServerManager.GetResolvedPrincipal(http) is { } resolved)
            identities.AddRange(resolved.Identities);
        return new System.Security.Claims.ClaimsPrincipal(identities);
    }

    /// <summary>The <c>eb:Messaging</c> plane onto the exchange, under <see cref="As4Headers"/> names only.</summary>
    private static void Headers(IMessage message, UserMessage user, As4Partner partner, SignatureVerification verification)
    {
        var h = message.Headers;
        h[As4Headers.MessageId] = user.MessageInfo.MessageId;
        if (user.MessageInfo.RefToMessageId is { } refTo) h[As4Headers.RefToMessageId] = refTo;
        h[As4Headers.Timestamp] = user.MessageInfo.Timestamp;
        h[As4Headers.ConversationId] = user.CollaborationInfo.ConversationId;

        var from = user.From.PartyIds[0];
        h[As4Headers.FromPartyId] = from.Value;
        if (from.Type is not null) h[As4Headers.FromPartyIdType] = from.Type;
        h[As4Headers.FromRole] = user.From.Role;
        var to = user.To.PartyIds[0];
        h[As4Headers.ToPartyId] = to.Value;
        if (to.Type is not null) h[As4Headers.ToPartyIdType] = to.Type;
        h[As4Headers.ToRole] = user.To.Role;

        h[As4Headers.Service] = user.CollaborationInfo.Service.Value;
        if (user.CollaborationInfo.Service.Type is not null) h[As4Headers.ServiceType] = user.CollaborationInfo.Service.Type;
        h[As4Headers.Action] = user.CollaborationInfo.Action;
        if (user.CollaborationInfo.AgreementRef is { } agreement)
        {
            h[As4Headers.AgreementRef] = agreement.Value;
            if (agreement.PMode is not null) h[As4Headers.PModeId] = agreement.PMode;
        }

        foreach (var property in user.MessageProperties)
            h[As4Headers.PropertyPrefix + property.Name] = property.Value;

        h[As4Headers.Partner] = partner.Name;
        h[As4Headers.SignatureValid] = true;
        h[As4Headers.SignerThumbprint] = verification.Signer.Thumbprint;
    }

    /// <summary>
    /// The first header block with <c>mustUnderstand="true"</c> that is neither <c>eb:Messaging</c> nor
    /// <c>wsse:Security</c> (SOAP 1.2 Part 1 §5.2.3), or null.
    /// </summary>
    private static string? EnvelopeProblem(XmlDocument envelope)
    {
        // SOAP 1.2 Part 1 §5: an Envelope, at most one Header, exactly one Body — and an AS4 message has its ebMS
        // header, so both are required. Anything else is not a message this receiver can answer in ebMS terms.
        var root = envelope.DocumentElement;
        if (root is null || root.LocalName != "Envelope" || root.NamespaceURI != EbmsNamespaces.Soap12)
            return "Not a SOAP 1.2 envelope.";
        foreach (var name in new[] { "Header", "Body" })
        {
            var count = root.ChildNodes.OfType<XmlElement>().Count(e => e.LocalName == name && e.NamespaceURI == EbmsNamespaces.Soap12);
            if (count != 1)
                return $"The SOAP 1.2 envelope carries {count} {name} elements; an AS4 message has exactly one.";
        }
        return null;
    }

    private const string SoapNextRole = "http://www.w3.org/2003/05/soap-envelope/role/next";
    private const string SoapUltimateReceiverRole = "http://www.w3.org/2003/05/soap-envelope/role/ultimateReceiver";

    private static string? UnderstoodHeaders(XmlDocument envelope)
    {
        var header = envelope.DocumentElement?.ChildNodes.OfType<XmlElement>()
            .FirstOrDefault(e => e.LocalName == "Header" && e.NamespaceURI == EbmsNamespaces.Soap12);
        if (header is null) return null;

        foreach (var block in header.ChildNodes.OfType<XmlElement>())
        {
            // SOAP 1.2 Part 1 §2.2: every node plays "next", the ultimate receiver also "ultimateReceiver" (the role of a
            // block without one); a block for "none" or another role is not addressed to us.
            var role = block.GetAttribute("role", EbmsNamespaces.Soap12);
            if (role.Length > 0 && role is not (SoapNextRole or SoapUltimateReceiverRole))
                continue;

            var understood = (block.LocalName == "Messaging" && block.NamespaceURI == EbmsNamespaces.Eb)
                             || (block.LocalName == "Security" && block.NamespaceURI == WsSecurityNames.Wsse);
            var mustUnderstand = block.GetAttribute("mustUnderstand", EbmsNamespaces.Soap12);
            if (!understood && mustUnderstand is "true" or "1")
                return $"{{{block.NamespaceURI}}}{block.LocalName}";
        }
        return null;
    }

    /// <summary>
    /// eDelivery AS4 1.16: <c>originalSender</c> and <c>finalRecipient</c> MUST be on every message (four-corner model).
    /// Missing or empty is <see cref="As4ErrorCode.ProcessingModeMismatch"/>, as Domibus answers it (PropertyProfileValidator);
    /// checked after the security checks, as there.
    /// </summary>
    private static void RequireFourCornerProperties(UserMessage user)
    {
        foreach (var required in new[] { As4Headers.OriginalSender, As4Headers.FinalRecipient })
        {
            var name = required[As4Headers.PropertyPrefix.Length..];
            if (string.IsNullOrEmpty(user.PropertyValue(name)))
                throw new As4ProcessingException(As4ErrorCode.ProcessingModeMismatch,
                    $"the message has no '{name}' property (eDelivery four-corner model).");
        }
    }

    /// <summary>Upper bound on one decompressed payload: ten times the request limit, at most 1 GB (docs/as4, 09 No. 12).</summary>
    private long MaxDecompressedBytes => Math.Min(_options.MaxRequestBodySize * 10, 1L << 30);

    private static Task WriteSignal(HttpContext http, (string ContentType, byte[] Body) signal) =>
        Write(http, (StatusCodes.Status200OK, signal.ContentType, signal.Body));

    private static async Task Write(HttpContext http, (int Status, string ContentType, byte[] Body) response)
    {
        http.Response.StatusCode = response.Status;
        http.Response.ContentType = response.ContentType;
        await http.Response.Body.WriteAsync(response.Body, http.RequestAborted).ConfigureAwait(false);
    }

    private static Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode? MapClientCertificateMode(As4ClientCertificateMode mode) => mode switch
    {
        As4ClientCertificateMode.AllowCertificate => Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.AllowCertificate,
        As4ClientCertificateMode.RequireCertificate => Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.RequireCertificate,
        _ => null,
    };

    /// <summary>
    /// The allow-list check for client certificates, as the SOAP receiver builds it. It replaces Kestrel's own check, and a
    /// pin is a constraint on top of validation, not instead of it: the certificate must be within its validity period and
    /// pass the node's revocation policy, as Holodeck ("directly trusted, check validity") and Domibus check a pinned
    /// client certificate. Chain building is skipped for the directly pinned certificate, as Holodeck skips it.
    /// </summary>
    private Func<System.Security.Cryptography.X509Certificates.X509Certificate2,
        System.Security.Cryptography.X509Certificates.X509Chain?,
        System.Net.Security.SslPolicyErrors, bool>? ThumbprintValidator(string? allowedThumbprints, RevocationPolicy revocation)
    {
        if (string.IsNullOrWhiteSpace(allowedThumbprints)) return null;
        var allowed = allowedThumbprints
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Replace(" ", string.Empty))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (certificate, _, _) =>
        {
            if (!allowed.Contains(certificate.Thumbprint)) return false;
            if (CertificateValidity.Problem(certificate, DateTimeOffset.UtcNow, "TLS client certificate", revocation) is not { } problem) return true;
            _logger?.LogWarning("AS4 receiver {Path}: refused an allow-listed client certificate in the TLS handshake: {Problem}", Path, problem);
            return false;
        };
    }
}
