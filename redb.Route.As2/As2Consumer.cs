using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MimeKit;
using MimeKit.Cryptography;
using redb.Route.Abstractions;
using redb.Route.As2.Crypto;
using redb.Route.As2.Mdn;
using redb.Route.Components;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Extensions;
using redb.Route.Http;
using redb.Route.Telemetry;

namespace redb.Route.As2;

/// <summary>
/// AS2 receive side (server): registers a POST route on the shared Kestrel host; checks the sender's identifiers,
/// decrypts and verifies the inbound S/MIME message against the pinned partner certificate, enforces the agreement,
/// computes the MIC, hands the business payload to the route, and answers with the MDN the request asked for:
/// synchronously, or posted to the receipt URL when the message authenticated and the agreement allows it. See
/// <c>docs/as2/02-DESIGN.md §6</c> and <c>docs/as2/REVIEW-2026-09-28.md</c>.
/// </summary>
internal sealed class As2Consumer : IConsumer
{
    // MIME headers that describe the S/MIME wrapper — they belong to the transport plane and must NOT be
    // copied into the exchange headers (the business Content-Type lives on Message.ContentType; leaking the
    // wrapper into Headers["Content-Type"] would override it on a downstream HTTP produce).
    private static readonly HashSet<string> WrapperHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Transfer-Encoding", "Content-Disposition", "Content-Length", "Content-Encoding",
        "Connection", "Keep-Alive", "Transfer-Encoding", "TE", "Trailer", "Upgrade", "Host",
    };

    // Credentials of the HTTP hop, dropped as the HTTP consumer drops a checked Authorization: a route that logs, forwards
    // or echoes its headers must not carry a caller's token or session on. The principal the host resolved from them is
    // on the exchange (ExchangePrincipal).
    private static readonly HashSet<string> CredentialHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "Cookie",
    };

    private readonly As2Endpoint _endpoint;
    private readonly IProcessor _processor;
    private readonly As2EndpointOptions _options;
    private readonly IAs2CryptoEngine _engine = new As2CryptoEngine();
    private readonly ILogger? _logger;
    private RouteRegistration? _registration;
    private As2Profile? _profile;

    // Delivers asynchronous MDNs; owned by this receiver, bounded by the endpoint's timeout.
    private HttpClient? _mdnClient;
    private IIdempotentRepository? _duplicates;
    private StreamCacheOptions? _spool;

    // Unregistering the route stops new deliveries; a partner message already inside the pipeline
    // still has to finish before Stop returns, and the listener stays up for the other routes.
    private readonly InflightDrainGuard _drain = new();

    public As2Consumer(As2Endpoint endpoint, IProcessor processor, As2EndpointOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = endpoint.Logger;
    }

    /// <inheritdoc />
    public IEndpoint Endpoint => _endpoint;

    private SharedHttpServerManager Server =>
        (_endpoint.Component as As2Component)?.Server
        ?? throw new InvalidOperationException("AS2 endpoint has no As2Component/server.");

    /// <summary>
    /// The TLS certificate of the receive server: the endpoint's own, else the one on the named
    /// connection factory (which is where its password belongs). Nothing here means the host
    /// default is asked next, inside the shared host.
    /// </summary>
    private (string? Path, string? Password) ResolveServerCertificate()
    {
        if (!string.IsNullOrEmpty(_options.SslCertPath))
            return (_options.SslCertPath, _options.SslCertPassword);

        if (!string.IsNullOrEmpty(_options.ConnectionFactory))
        {
            var factory = _endpoint.Context.GetRequiredFromRegistry<As2ConnectionFactory>(_options.ConnectionFactory);
            if (!string.IsNullOrEmpty(factory.SslCertPath))
                return (factory.SslCertPath, factory.SslCertPassword);
        }

        return (null, null);
    }

    /// <inheritdoc />
    public async Task Start(CancellationToken ct = default)
    {
        // The agreement is resolved once, here: a misnamed or incomplete one stops the route from starting instead of
        // answering the partner's first message with a negative MDN.
        var profile = As2Profile.Resolve(_endpoint.Context, _options, _endpoint.Uri.Path);
        if (profile.MdnMode == As2MdnMode.Async && profile.AsyncMdnAllowedHosts.Count == 0)
            throw new InvalidOperationException(
                $"AS2 receive endpoint {_endpoint.Uri.Path}: MdnMode is async, so AsyncMdnAllowedHosts must name the hosts receipts may be posted to; " +
                "the address itself comes from the partner's request.");
        _profile = profile;
        _spool = _endpoint.Context.GetStreamCacheOptions();
        // Optional here (RFC 4130 does not mandate it, unlike eDelivery AS4); named, as the AS4 and S3 receivers name it.
        _duplicates = string.IsNullOrWhiteSpace(_options.IdempotentRepository) ? null
            : (_endpoint.Context ?? throw new InvalidOperationException($"AS2 receive endpoint {_endpoint.Uri.Path} has no route context to resolve its idempotent repository from."))
                .GetIdempotentRepositoryProvider().Get(_options.IdempotentRepository);
        _mdnClient = new HttpClient
        {
            Timeout = _options.Timeout > 0 ? TimeSpan.FromMilliseconds(_options.Timeout) : Timeout.InfiniteTimeSpan,
        };

        _drain.Start(ct);

        var host = _options.Host;
        var port = _options.Port;
        var path = _endpoint.Uri.Path;   // e.g. "/inbound/orders" — kept intact by the DSL

        // The TLS server certificate travels with the registration. Until Ф14 it did not, so an
        // as2s:// receiver opened a PLAINTEXT port while PartnerUrl advertised https:// to the
        // trading partner.
        var (certPath, certPassword) = ResolveServerCertificate();

        _registration = Server.RegisterRoute(host, port, path, "POST", HandleRequest,
            _options.UseTls, certPath, certPassword,
            maxRequestBodySize: _options.MaxRequestBodySize,
            concurrencyLimit: ConcurrencyLimitOptions.FromEndpoint(
                _options.MaxConcurrentRequests, _options.RequestQueueLimit,
                _options.RejectStatusCode, _options.RetryAfterSeconds,
                onRejected: _endpoint.RecordRejected));
        await Server.EnsureStarted(host, port, ct).ConfigureAwait(false);

        _logger?.LogInformation("AS2 consumer started: {Host}:{Port}{Path}", host, port, path);
    }

    /// <inheritdoc />
    public async Task Stop(CancellationToken ct = default)
    {
        if (_registration is not null)
        {
            Server.UnregisterRoute(_registration);
            _registration = null;
            // The drain also waits for asynchronous MDNs still being posted: each is counted like a request.
            await _drain.DrainAsync(ct, _logger, $"as2://{_options.Host}:{_options.Port}").ConfigureAwait(false);
            await Server.StopIfEmpty(_options.Host, _options.Port, ct).ConfigureAwait(false);
        }
        _mdnClient?.Dispose();
        _mdnClient = null;
        _logger?.LogInformation("AS2 consumer stopped: {Host}:{Port}", _options.Host, _options.Port);
    }

    private async Task HandleRequest(HttpContext http)
    {
        // Counted for the drain: unregistering the route stops new deliveries, but a partner's
        // message already being verified, decrypted and routed must finish before Stop returns.
        _drain.Increment();
        try
        {
            await HandleRequestCore(http).ConfigureAwait(false);
        }
        finally
        {
            _drain.Decrement();
        }
    }

    private async Task HandleRequestCore(HttpContext http)
    {
        using var span = StartReceiveSpan(_endpoint, http.Request);
        var request = http.Request;

        var profile = _profile!;
        var reference = http.TraceIdentifier;
        var originalMessageId = request.Headers[As2Headers.MessageId].ToString();
        var receipt = ReceiptRequest.Of(request, profile);
        if (receipt.RequestedMicAlgs is { Count: > 0 } asked && !asked.Contains(receipt.MicAlg, StringComparer.OrdinalIgnoreCase))
            _logger?.LogWarning("AS2 receiver {Path}: message {MessageId} asks for micalg {Asked}; none is usable here, the MIC is {Used}.",
                request.Path, originalMessageId, string.Join(", ", asked), receipt.MicAlg);

        As2Mic? mic = null;
        var signatureValid = false;
        var wasSigned = false;
        var wasEncrypted = false;
        var authenticated = false;   // the sender is the agreed partner, as far as the agreement can tell
        string? failure = null;      // RFC 4130 §7.4.3 modifier of a message that was not processed
        IExchange? exchange = null;
        var claimed = false;         // this request holds the duplicate-detection claim of its Message-ID
        var duplicate = false;       // the Message-ID was received and processed before
        var processed = false;       // the route processed the document

        // The request and every stage unwrapped from it live in spools (the core stream cache: memory, or a temporary
        // file past the threshold), parsed by MimeKit in place; all of them go when the request is answered.
        StreamCache? mime = null;
        var spools = new List<StreamCache>();

        try
        {
            // The MIME headers of the wrapper and the body, in one spool; bounded by maxRequestBodySize.
            mime = await As2Http.ReadBodyAsync(http, _options.MaxRequestBodySize, MimeHeaderPrefix(request), _spool!, http.RequestAborted)
                .ConfigureAwait(false);
            if (mime is null)
            {
                _logger?.LogWarning("AS2 receiver {Path}: request refused, its body is over maxRequestBodySize {Limit} bytes (ref {Reference}).",
                    request.Path, _options.MaxRequestBodySize, reference);
                _endpoint.RecordError();
                span.Activity?.SetStatus(ActivityStatusCode.Error, "request body over maxRequestBodySize");
                return;
            }

            // The agreement names both parties (RFC 4130 §6.2): a message from another sender, or for another
            // recipient, is not ours to process, whatever it carries.
            var from = request.Headers[As2Headers.As2From].ToString();
            var to = request.Headers[As2Headers.As2To].ToString();
            if (!IsAgreedIdentifier(from, profile.As2To) || !IsAgreedIdentifier(to, profile.As2From))
                throw new As2DispositionException(As2Disposition.AuthenticationFailed,
                    $"AS2-From '{from}' and AS2-To '{to}' are not the agreement's '{profile.As2To}' and '{profile.As2From}'.");

            // Unwrap: decrypt (outer) → verify signature (+ MIC over the signed part) → decompress (inner).
            var entity = MimeEntity.Load(mime, persistent: true);

            if (entity is ApplicationPkcs7Mime { SecureMimeType: SecureMimeType.EnvelopedData } enveloped)
            {
                if (profile.OurCertificate is null)
                    throw new As2DispositionException(As2Disposition.DecryptionFailed,
                        "the message is encrypted and the agreement has no certificate of ours to decrypt it.");
                try { entity = Unwrap(spools, s => _engine.DecryptTo(enveloped, profile.OurCertificate, s)); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    throw new As2DispositionException(As2Disposition.DecryptionFailed, $"decryption failed: {e.Message}", e);
                }
                wasEncrypted = true;
            }

            if (entity is MultipartSigned signed)
            {
                if (profile.PartnerCertificate is null)
                    throw new As2DispositionException(As2Disposition.AuthenticationFailed,
                        "the message is signed and the agreement has no partner certificate to verify it.");
                if (As2CertificateValidity.Problem(profile.PartnerCertificate, DateTimeOffset.UtcNow, "The partner's signing certificate") is { } invalid)
                    throw new As2DispositionException(As2Disposition.AuthenticationFailed, invalid);
                try { signatureValid = _engine.Verify(signed, profile.PartnerCertificate); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    throw new As2DispositionException(As2Disposition.AuthenticationFailed, $"signature verification failed: {e.Message}", e);
                }
                mic = _engine.ComputeReceivedMic(signed[0], receipt.MicAlg);
                entity = signed[0];
                wasSigned = true;
            }

            if (entity is ApplicationPkcs7Mime { SecureMimeType: SecureMimeType.CompressedData } compressed)
            {
                try { entity = Unwrap(spools, s => _engine.DecompressTo(compressed, s)); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    throw new As2DispositionException(As2Disposition.DecompressionFailed, $"decompression failed: {e.Message}", e);
                }
            }

            mic ??= _engine.ComputeMic(entity, receipt.MicAlg, includeHeaders: false);

            // ENFORCE the profile before handing the payload to the route: a message that the partnership
            // requires to be signed/encrypted but is not, or whose signature did not verify, is rejected with a
            // negative MDN — never processed as if trusted.
            if (wasSigned && !signatureValid)
                throw new As2DispositionException(As2Disposition.AuthenticationFailed,
                    "the signature does not verify against the partner certificate (another signer, or altered content).");
            if (profile.Encrypt && !wasEncrypted)
                throw new As2DispositionException(As2Disposition.InsufficientMessageSecurity,
                    "the message is not encrypted and the agreement requires encryption.");
            if (profile.Sign && !wasSigned)
                throw new As2DispositionException(As2Disposition.InsufficientMessageSecurity,
                    "the message is not signed and the agreement requires a signature.");
            authenticated = true;

            // Duplicate detection, the AS4 receiver's two-phase claim (Add, then Confirm or Remove): claimed only now, so a
            // forged copy cannot burn the id of the real message; taken uncancelled, as Confirm and Remove are.
            if (_duplicates is not null)
            {
                if (string.IsNullOrWhiteSpace(originalMessageId))
                    throw new As2DispositionException(As2Disposition.UnexpectedProcessingError,
                        "the message has no Message-ID (RFC 4130 requires one), and duplicate detection needs it.");
                claimed = await _duplicates.Add(originalMessageId, CancellationToken.None).ConfigureAwait(false);
                duplicate = !claimed;
                if (duplicate)
                    _logger?.LogInformation("AS2 receiver {Path}: message {MessageId} is a duplicate; answered, not delivered again.",
                        request.Path, originalMessageId);
            }

            if (!duplicate)
            {
                // Extract the business payload.
                var (payload, payloadContentType) = ExtractPayload(entity, _spool!, _options.StreamBody);

                // Build the exchange. Content-Type comes from the INNER part (Message.ContentType); the wrapper
                // Content-Type is deliberately NOT copied into the headers.
                var message = new Message(payload) { ContentType = payloadContentType };
                CopyInboundHeaders(request, message);
                // The header reports a verification: an unsigned message had none.
                message.Headers[As2Headers.SignatureValid] = wasSigned && signatureValid;
                if (mic is not null)
                {
                    message.Headers[As2Headers.Mic] = mic.Value.Digest;
                    message.Headers[As2Headers.MicAlg] = mic.Value.Algorithm;
                }
                if (http.Connection.RemoteIpAddress is not null)
                    message.Headers[As2Headers.RemoteAddress] = http.Connection.RemoteIpAddress.ToString();
                if (!string.IsNullOrEmpty(_options.ConnectionFactory))
                    message.Headers[As2Headers.PartnerName] = _options.ConnectionFactory;

                exchange = Exchange.Create(message, _endpoint.ScopeFactory);
                exchange.Pattern = ExchangePattern.InOut;
                ExchangePrincipal.Set(exchange, SharedHttpServerManager.GetResolvedPrincipal(http));

                // Cancelled when the sender goes away, and when a stop outlasts the drain timeout.
                using (var processing = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, _drain.ProcessingToken))
                    await _processor.Process(exchange, processing.Token).ConfigureAwait(false);

                if (exchange.EndedInFailure())
                {
                    // No RecordError: the core StatisticsProcessor saw the same exchange.Exception.
                    _logger?.LogWarning(exchange.Exception, "AS2 receiver {Path}: route failed for message {MessageId} (ref {Reference}).",
                        request.Path, originalMessageId, reference);
                    if (exchange.Exception is { } routeFailure) span.Activity.RecordFailure(routeFailure);
                    failure = As2Disposition.UnexpectedProcessingError;
                }
                else
                {
                    processed = true;
                }
            }
        }
        catch (As2DispositionException e)
        {
            _logger?.LogWarning(e, "AS2 receiver {Path}: message {MessageId} refused with {Disposition} (ref {Reference}): {Detail}",
                request.Path, originalMessageId, e.Modifier, reference, e.Message);
            _endpoint.RecordError(e);
            span.Activity.RecordFailure(e);
            failure = e.Modifier;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "AS2 receiver {Path}: message {MessageId} not processed (ref {Reference}).",
                request.Path, originalMessageId, reference);
            _endpoint.RecordError(ex);
            span.Activity.RecordFailure(ex);
            failure = As2Disposition.UnexpectedProcessingError;
        }
        finally
        {
            if (exchange is not null)
                await exchange.DisposeAsync().ConfigureAwait(false);
            foreach (var spool in spools)
                await spool.DisposeAsync().ConfigureAwait(false);
            if (mime is not null)
                await mime.DisposeAsync().ConfigureAwait(false);
            // Processed: the id stays. Anything else (the route failed, threw, or was cancelled): released, so the
            // partner's resend is delivered and not taken for a duplicate.
            if (claimed)
                await SettleClaimAsync(originalMessageId, processed).ConfigureAwait(false);
        }

        // RFC 4130 §7.3: an MDN only when the sender asked for one (Disposition-Notification-To), and never when the
        // agreement says none.
        if (profile.MdnMode == As2MdnMode.None || !receipt.Requested)
        {
            http.Response.StatusCode = StatusCodes.Status200OK;
            return;
        }

        // Signed when the request asks for a signed receipt, with the micalg it asked for. A failure carries its code
        // and our reference only.
        var sign = receipt.Signed && profile.OurCertificate is not null;
        if (receipt.Signed && profile.OurCertificate is null)
            _logger?.LogWarning("AS2 receiver {Path}: message {MessageId} asks for a signed MDN and the agreement has no certificate of ours; " +
                "the MDN is unsigned (ref {Reference}).", request.Path, originalMessageId, reference);
        var (mdnContentType, mdnCte, mdnBody) = MdnBuilder.Build(
            originalMessageId, profile.As2From, profile.As2To, mic, failure, reference,
            warning: duplicate ? "duplicate-document" : null,
            signer: sign ? _engine : null,
            signerCert: sign ? profile.OurCertificate : null,
            signAlg: receipt.MicAlg);

        // Async MDN: posted only for a sender that authenticated, in an agreement that says async, to a host the
        // agreement names — the address comes from the request, so anything less lets any caller make us POST (a
        // receipt signed by our key) wherever it likes. Otherwise the receipt is the response to this request.
        var receiptUrl = http.Request.Headers[As2Headers.ReceiptDeliveryOption].ToString();
        if (!string.IsNullOrEmpty(receiptUrl) && AsyncReceiptRefusal(receiptUrl, authenticated, profile) is { } refusal)
        {
            _logger?.LogWarning("AS2 receiver {Path}: asynchronous MDN for message {MessageId} not posted to '{Url}': {Reason}; " +
                "the MDN is returned in the response (ref {Reference}).", request.Path, originalMessageId, receiptUrl, refusal, reference);
            receiptUrl = "";
        }

        if (!string.IsNullOrEmpty(receiptUrl))
        {
            http.Response.StatusCode = StatusCodes.Status200OK;
            // Answered first, posted after: the sender's wait for 200 must not depend on its own MDN endpoint. The
            // post is counted for the drain and cancelled by a forced stop, not by this request's end.
            _drain.Increment();
            _ = Task.Run(async () =>
            {
                try { await PostAsyncMdn(receiptUrl, mdnContentType, mdnCte, mdnBody, profile, originalMessageId, _drain.ProcessingToken).ConfigureAwait(false); }
                finally { _drain.Decrement(); }
            });
        }
        else
        {
            var response = http.Response;
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = mdnContentType;
            response.Headers[As2Headers.As2Version] = "1.2";
            if (!string.IsNullOrEmpty(profile.As2From)) response.Headers[As2Headers.As2From] = profile.As2From;
            if (!string.IsNullOrEmpty(profile.As2To)) response.Headers[As2Headers.As2To] = profile.As2To;
            response.Headers[As2Headers.MessageId] = $"<{Guid.NewGuid():N}@redb.route>";
            if (!string.IsNullOrEmpty(mdnCte)) response.Headers["Content-Transfer-Encoding"] = mdnCte;
            await response.Body.WriteAsync(mdnBody, http.RequestAborted).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Why an asynchronous MDN is not posted to <paramref name="url"/>, or null when it is. The host must be one the
    /// agreement lists (<see cref="As2ConnectionFactory.AsyncMdnAllowedHosts"/>) — a name is trusted as the operator
    /// wrote it; what it resolves to is not checked again.
    /// </summary>
    private static string? AsyncReceiptRefusal(string url, bool authenticated, As2Profile profile)
    {
        if (!authenticated)
            return "the message did not authenticate";
        if (profile.MdnMode != As2MdnMode.Async)
            return $"the agreement's MDN mode is {profile.MdnMode}";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return "it is not an absolute http(s) URL";
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return "it carries user information";
        var host = uri.Host.Trim('[', ']');
        if (!profile.AsyncMdnAllowedHosts.Any(h => string.Equals(h.Trim('[', ']'), host, StringComparison.OrdinalIgnoreCase)))
            return $"host '{host}' is not in AsyncMdnAllowedHosts";
        return null;
    }

    /// <summary>
    /// Confirms the claim of a processed message, or releases one the route did not process (as the AS4 and S3
    /// receivers do). The answer goes out either way, so a failure here is logged: a failed confirm leaves the claim from
    /// Add in place; a failed release leaves the id marked until it is cleaned up.
    /// </summary>
    private async Task SettleClaimAsync(string messageId, bool processed)
    {
        try
        {
            if (processed) await _duplicates!.Confirm(messageId, CancellationToken.None).ConfigureAwait(false);
            else await _duplicates!.Remove(messageId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger?.LogWarning(e, "AS2 receiver {Path}: {Action} the duplicate claim of message {MessageId} failed.",
                _endpoint.Uri.Path, processed ? "confirming" : "releasing", messageId);
        }
    }

    /// <summary>
    /// Whether a received <c>AS2-From</c>/<c>AS2-To</c> is the agreed identifier: RFC 4130 §6.2 allows a quoted-string
    /// and compares case-sensitively.
    /// </summary>
    internal static bool IsAgreedIdentifier(string? received, string agreed)
    {
        if (string.IsNullOrEmpty(agreed) || string.IsNullOrWhiteSpace(received)) return false;
        var value = received.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            value = value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
        return string.Equals(value, agreed, StringComparison.Ordinal);
    }

    private async Task PostAsyncMdn(string url, string contentType, string? transferEncoding, byte[] body, As2Profile profile,
        string originalMessageId, CancellationToken ct)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        if (!string.IsNullOrEmpty(transferEncoding))
            content.Headers.TryAddWithoutValidation("Content-Transfer-Encoding", transferEncoding);

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        if (!string.IsNullOrEmpty(profile.As2From)) request.Headers.TryAddWithoutValidation(As2Headers.As2From, profile.As2From);
        if (!string.IsNullOrEmpty(profile.As2To)) request.Headers.TryAddWithoutValidation(As2Headers.As2To, profile.As2To);
        request.Headers.TryAddWithoutValidation(As2Headers.MessageId, $"<{Guid.NewGuid():N}@redb.route>");

        // Runs detached from the request, so nothing above it would see an exception: every outcome is logged here.
        try
        {
            var client = _mdnClient ?? throw new InvalidOperationException("the receiver is stopped.");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                _logger?.LogWarning("AS2 asynchronous MDN for message {MessageId} to {Url} was answered {Status}.",
                    originalMessageId, url, (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "AS2 asynchronous MDN for message {MessageId} to {Url} was not delivered.", originalMessageId, url);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The Consumer span of a received request, on the core contract (<see cref="RouteTelemetryExtensions.StartConsumerSpan{TCarrier}"/>):
    /// the host's request span when the application traces ASP.NET Core, else the sender's <c>traceparent</c>, else a
    /// root; the sender's baggage is put back; <c>EnableTelemetry=false</c> opens nothing. Named after its destination,
    /// the receive path.
    /// </summary>
    internal static TransportSpan StartReceiveSpan(As2Endpoint endpoint, HttpRequest request) =>
        RouteTelemetryExtensions.StartConsumerSpan(
            endpoint.Context, $"{endpoint.Uri.Path} receive", ActivityKind.Consumer,
            "messaging.system", "as2", endpoint.Uri.NormalizedKey,
            request.Headers, static (headers, name) => headers.TryGetValue(name, out var value) ? value.ToString() : null,
            InboundParent.HostRequest, destination: endpoint.Uri.Path, operation: "receive");

    /// <summary>The MIME headers of the inbound entity, rebuilt from the HTTP headers that carry them.</summary>
    private static byte[] MimeHeaderPrefix(HttpRequest request)
    {
        var sb = new StringBuilder();
        sb.Append("Content-Type: ").Append(request.ContentType ?? "application/octet-stream").Append("\r\n");
        if (request.Headers.TryGetValue("Content-Transfer-Encoding", out var cte))
            sb.Append("Content-Transfer-Encoding: ").Append(cte.ToString()).Append("\r\n");
        if (request.Headers.TryGetValue("Content-Disposition", out var cd))
            sb.Append("Content-Disposition: ").Append(cd.ToString()).Append("\r\n");
        sb.Append("\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Runs one unwrapping step into a new spool and parses its output in place (MimeKit's persistent mode keeps the
    /// parts as views of the spool). The spool lives until the request is answered; a failed step leaves nothing behind.
    /// </summary>
    private MimeEntity Unwrap(List<StreamCache> spools, Action<Stream> step)
    {
        var spool = new StreamCache(_spool!);
        spools.Add(spool);
        step(spool);
        spool.CompleteWriting();
        spool.Position = 0;
        return MimeEntity.Load(spool, persistent: true);
    }

    /// <summary>
    /// The business payload, decoded into a spool of its own. With <paramref name="streamBody"/> the spool is the body
    /// (the exchange closes it); otherwise its bytes are, and the spool goes at once.
    /// </summary>
    private static (object payload, string? contentType) ExtractPayload(MimeEntity entity, StreamCacheOptions options, bool streamBody)
    {
        var spool = new StreamCache(options);
        var keep = false;
        try
        {
            string? contentType;
            if (entity is MimePart { Content: not null } part)
            {
                part.Content.DecodeTo(spool);
                contentType = part.ContentType?.MimeType;
            }
            else
            {
                // Non-leaf payload (rare): the whole entity.
                entity.WriteTo(spool);
                contentType = entity.ContentType?.MimeType;
            }
            spool.CompleteWriting();
            spool.Position = 0;

            if (streamBody)
            {
                keep = true;
                return (spool, contentType);
            }

            var bytes = new byte[spool.Length];
            spool.ReadExactly(bytes);
            return (bytes, contentType);
        }
        finally
        {
            if (!keep) spool.Dispose();
        }
    }

    /// <summary>Copies AS2/business request headers verbatim, skipping the S/MIME wrapper + hop-by-hop set.</summary>
    private static void CopyInboundHeaders(HttpRequest request, IMessage message)
    {
        foreach (var header in request.Headers)
        {
            if (header.Key.StartsWith(':')) continue;              // pseudo-headers
            if (WrapperHeaders.Contains(header.Key)) continue;      // wrapper / hop-by-hop
            if (CredentialHeaders.Contains(header.Key)) continue;   // they authenticated the hop, not the document
            message.Headers[header.Key] = header.Value.Count == 1 ? header.Value[0] : header.Value.ToString();
        }
    }
}
