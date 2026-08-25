using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MimeKit;
using MimeKit.Cryptography;
using redb.Route.Abstractions;
using redb.Route.As2.Crypto;
using redb.Route.As2.Mdn;
using redb.Route.Core;
using redb.Route.Http;
using redb.Route.Telemetry;

namespace redb.Route.As2;

/// <summary>
/// AS2 receive side (server): registers a POST route on the shared Kestrel host, decrypts + verifies the
/// inbound S/MIME message, computes the MIC, hands the business payload to the route, and returns a
/// synchronous MDN. See <c>docs/as2/02-DESIGN.md §6</c>. Ф3: receive + synchronous MDN (unsigned);
/// signed MDN is Ф4, async MDN is Ф5.
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

    // Shared client for delivering asynchronous MDNs back to partners.
    private static readonly HttpClient MdnHttpClient = new();

    private readonly As2Endpoint _endpoint;
    private readonly IProcessor _processor;
    private readonly As2EndpointOptions _options;
    private readonly IAs2CryptoEngine _engine = new As2CryptoEngine();
    private readonly ILogger? _logger;
    private RouteRegistration? _registration;

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

    /// <inheritdoc />
    public async Task Start(CancellationToken ct = default)
    {
        var host = _options.Host;
        var port = _options.Port;
        var path = _endpoint.Uri.Path;   // e.g. "/inbound/orders" — kept intact by the DSL

        _registration = Server.RegisterRoute(host, port, path, "POST", HandleRequest, _options.UseTls);
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
            await Server.StopIfEmpty(_options.Host, _options.Port, ct).ConfigureAwait(false);
        }
        _logger?.LogInformation("AS2 consumer stopped: {Host}:{Port}", _options.Host, _options.Port);
    }

    private async Task HandleRequest(HttpContext http)
    {
        using var activity = StartConsumerActivity(http.Request);
        var request = http.Request;

        byte[] bodyBytes;
        using (var ms = new MemoryStream())
        {
            await request.Body.CopyToAsync(ms, http.RequestAborted).ConfigureAwait(false);
            bodyBytes = ms.ToArray();
        }

        var profile = As2Profile.Resolve(_endpoint.Context, _options);
        var originalMessageId = request.Headers[As2Headers.MessageId].ToString();

        As2Mic? mic = null;
        var signatureValid = true;
        var wasSigned = false;
        var wasEncrypted = false;
        string? errorText = null;
        IExchange? exchange = null;

        try
        {
            // Unwrap: decrypt (outer) → verify signature (+ MIC over the signed part) → decompress (inner).
            var entity = LoadInbound(request, bodyBytes);

            if (entity is ApplicationPkcs7Mime { SecureMimeType: SecureMimeType.EnvelopedData } enveloped)
            {
                if (profile.OurCertificate is null)
                    throw new InvalidOperationException("No certificate configured to decrypt the AS2 message.");
                entity = _engine.Decrypt(enveloped, profile.OurCertificate);
                wasEncrypted = true;
            }

            if (entity is MultipartSigned signed)
            {
                if (profile.PartnerCertificate is null)
                    throw new InvalidOperationException("No partner certificate configured to verify the signature.");
                signatureValid = _engine.Verify(signed, profile.PartnerCertificate);
                mic = _engine.ComputeMic(signed[0], profile.SignAlg, includeHeaders: true);
                entity = signed[0];
                wasSigned = true;
            }

            if (entity is ApplicationPkcs7Mime { SecureMimeType: SecureMimeType.CompressedData } compressed)
                entity = _engine.Decompress(compressed);

            mic ??= _engine.ComputeMic(entity, profile.SignAlg, includeHeaders: false);

            // ENFORCE the profile before handing the payload to the route: a message that the partnership
            // requires to be signed/encrypted but is not, or whose signature did not verify, is rejected with a
            // negative MDN (RFC 4130 authentication/integrity-check-failed) — never processed as if trusted.
            if (profile.Encrypt && !wasEncrypted)
                throw new InvalidOperationException("AS2 message is not encrypted, but the partnership requires encryption.");
            if (profile.Sign && !wasSigned)
                throw new InvalidOperationException("AS2 message is not signed, but the partnership requires a signature.");
            if (wasSigned && !signatureValid)
                throw new InvalidOperationException("AS2 message signature verification failed (untrusted or tampered).");

            // Extract the business payload.
            var (payload, payloadContentType) = ExtractPayload(entity);

            // Build the exchange. Content-Type comes from the INNER part (Message.ContentType); the wrapper
            // Content-Type is deliberately NOT copied into the headers.
            var message = new Message(payload) { ContentType = payloadContentType };
            CopyInboundHeaders(request, message);
            message.Headers[As2Headers.SignatureValid] = signatureValid;
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

            await _processor.Process(exchange, http.RequestAborted).ConfigureAwait(false);

            if (exchange.Exception is not null && !exchange.ExceptionHandled)
                errorText = exchange.Exception.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "AS2 receive processing failed: {Path}", request.Path);
            errorText = ex.Message;
            signatureValid = false;
        }
        finally
        {
            if (exchange is not null)
                await exchange.DisposeAsync().ConfigureAwait(false);
        }

        if (_options.MdnMode == As2MdnMode.None)
        {
            http.Response.StatusCode = StatusCodes.Status200OK;
            return;
        }

        // Build the MDN (signed when the profile asks for it).
        var success = errorText is null;
        var sign = profile.SignedMdn && profile.OurCertificate is not null;
        var (mdnContentType, mdnCte, mdnBody) = MdnBuilder.Build(
            originalMessageId, profile.As2From, profile.As2To, mic, success, errorText,
            signer: sign ? _engine : null,
            signerCert: sign ? profile.OurCertificate : null,
            signAlg: profile.SignAlg);

        // Async MDN: the sender asked us to deliver the receipt out-of-band. Ack the POST with 200, then
        // POST the MDN to the Receipt-Delivery-Option URL. Sync MDN: return it in this response.
        var receiptUrl = http.Request.Headers[As2Headers.ReceiptDeliveryOption].ToString();
        if (!string.IsNullOrEmpty(receiptUrl))
        {
            http.Response.StatusCode = StatusCodes.Status200OK;
            // The receipt URL comes verbatim from the (unauthenticated-at-transport) request: validate it so
            // it can't drive an SSRF POST to a loopback/link-local/private-range target (169.254.169.254, etc.).
            if (IsSafeReceiptUrl(receiptUrl))
                await PostAsyncMdn(receiptUrl, mdnContentType, mdnCte, mdnBody, profile, http.RequestAborted).ConfigureAwait(false);
            else
                _logger?.LogWarning("AS2 async MDN not delivered: unsafe Receipt-Delivery-Option URL '{Url}'.", receiptUrl);
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
    /// True when the async-MDN receipt URL is an acceptable http(s) target. Blocks link-local literal IPs
    /// (169.254.0.0/16 and IPv6 fe80::/10) — the cloud-metadata SSRF class — reached via the
    /// attacker-controlled Receipt-Delivery-Option header. Loopback and private ranges are allowed, since
    /// co-located and internal-network AS2 partners are legitimate; a stricter partner-host allowlist is a
    /// deployment choice, and DNS-rebinding (a hostname resolving to a blocked IP) is out of scope here.
    /// </summary>
    private static bool IsSafeReceiptUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (System.Net.IPAddress.TryParse(uri.Host, out var ip))
        {
            if (ip.IsIPv6LinkLocal) return false;
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) return false;   // 169.254.0.0/16 (incl. 169.254.169.254 metadata)
            }
        }
        return true;
    }

    private async Task PostAsyncMdn(string url, string contentType, string? transferEncoding, byte[] body, As2Profile profile, CancellationToken ct)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        if (!string.IsNullOrEmpty(transferEncoding))
            content.Headers.TryAddWithoutValidation("Content-Transfer-Encoding", transferEncoding);

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        if (!string.IsNullOrEmpty(profile.As2From)) request.Headers.TryAddWithoutValidation(As2Headers.As2From, profile.As2From);
        if (!string.IsNullOrEmpty(profile.As2To)) request.Headers.TryAddWithoutValidation(As2Headers.As2To, profile.As2To);
        request.Headers.TryAddWithoutValidation(As2Headers.MessageId, $"<{Guid.NewGuid():N}@redb.route>");

        try
        {
            using var response = await MdnHttpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                _logger?.LogWarning("Async MDN delivery to {Url} returned {Status}", url, (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Async MDN delivery to {Url} failed", url);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Starts a Consumer span on the shared route source, parented to the inbound W3C traceparent.</summary>
    private static Activity? StartConsumerActivity(HttpRequest request)
    {
        ActivityContext parent = default;
        if (request.Headers.TryGetValue("traceparent", out var tp) && !string.IsNullOrEmpty(tp))
        {
            var traceState = request.Headers.TryGetValue("tracestate", out var ts) ? ts.ToString() : null;
            ActivityContext.TryParse(tp.ToString(), traceState, out parent);
        }

        var activity = RouteActivitySource.Source.StartActivity("AS2 receive", ActivityKind.Consumer, parent);
        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("messaging.system", "as2");
            activity.SetTag("messaging.operation", "receive");
        }
        return activity;
    }

    /// <summary>Reconstructs the inbound MIME entity from the request's MIME headers + body.</summary>
    private static MimeEntity LoadInbound(HttpRequest request, byte[] body)
    {
        var sb = new StringBuilder();
        sb.Append("Content-Type: ").Append(request.ContentType ?? "application/octet-stream").Append("\r\n");
        if (request.Headers.TryGetValue("Content-Transfer-Encoding", out var cte))
            sb.Append("Content-Transfer-Encoding: ").Append(cte.ToString()).Append("\r\n");
        if (request.Headers.TryGetValue("Content-Disposition", out var cd))
            sb.Append("Content-Disposition: ").Append(cd.ToString()).Append("\r\n");
        sb.Append("\r\n");

        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes(sb.ToString()));
        stream.Write(body);
        stream.Position = 0;
        return MimeEntity.Load(stream);
    }

    private static (byte[] payload, string? contentType) ExtractPayload(MimeEntity entity)
    {
        if (entity is MimePart { Content: not null } part)
        {
            using var ms = new MemoryStream();
            part.Content.DecodeTo(ms);
            return (ms.ToArray(), part.ContentType?.MimeType);
        }

        // Non-leaf payload (rare) — serialize the whole entity.
        using var full = new MemoryStream();
        entity.WriteTo(full);
        return (full.ToArray(), entity.ContentType?.MimeType);
    }

    /// <summary>Copies AS2/business request headers verbatim, skipping the S/MIME wrapper + hop-by-hop set.</summary>
    private static void CopyInboundHeaders(HttpRequest request, IMessage message)
    {
        foreach (var header in request.Headers)
        {
            if (header.Key.StartsWith(':')) continue;              // pseudo-headers
            if (WrapperHeaders.Contains(header.Key)) continue;      // wrapper / hop-by-hop
            message.Headers[header.Key] = header.Value.Count == 1 ? header.Value[0] : header.Value.ToString();
        }
    }
}
