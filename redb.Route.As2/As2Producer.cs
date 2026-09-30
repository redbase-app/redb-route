using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using MimeKit;
using redb.Route.Abstractions;
using redb.Route.As2.Crypto;
using redb.Route.As2.Mdn;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.As2;

/// <summary>
/// AS2 send side (client): builds an S/MIME message (compress → sign → encrypt per the partner profile) and
/// POSTs it to the partner over HTTP(S). Extends <see cref="ConnectableProducer"/> with an internal
/// <see cref="HttpClient"/>; presents our certificate for mutual TLS. The computed MIC and Message-ID are
/// written back to the exchange; a synchronous MDN is verified here (<see cref="Mdn.MdnVerdict"/>), an asynchronous
/// one is correlated by the <c>ReceiveMdn</c> endpoint. See <c>docs/as2/02-DESIGN.md §7</c>.
/// </summary>
internal sealed class As2Producer : ConnectableProducer
{
    private readonly As2Endpoint _endpoint;
    private readonly As2EndpointOptions _options;
    private readonly IAs2CryptoEngine _engine = new As2CryptoEngine();
    private HttpClient? _http;
    private As2Profile? _profile;

    public As2Producer(As2Endpoint endpoint, As2EndpointOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    protected override IEndpoint ProducerEndpoint => _endpoint;
    // Same invariant as HttpProducer: the name reaches started/stopped logs, and a partner URL
    // with basic-auth carries a userinfo password no [Sensitive] attribute can reach.
    protected override string ProducerName => $"as2:{EndpointUri.Sanitize(_options.PartnerUrl)}";

    /// <inheritdoc />
    protected override Task ConnectAsync(CancellationToken ct)
    {
        // The agreement is resolved and checked once, here: a broken one stops the producer from starting instead of
        // failing every message.
        var profile = As2Profile.Resolve(_endpoint.Context, _options, EndpointUri.Sanitize(_options.PartnerUrl));
        if (string.IsNullOrEmpty(profile.PartnerUrl))
            throw new InvalidOperationException("AS2 producer has no partner URL (set it on the URI or on the connection factory).");
        if (!string.IsNullOrEmpty(_options.ConnectionFactory) && !string.IsNullOrEmpty(_options.PartnerUrl)
            && !string.Equals(profile.PartnerUrl.TrimEnd('/'), _options.PartnerUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"AS2 producer {EndpointUri.Sanitize(_options.PartnerUrl)}: connection factory '{_options.ConnectionFactory}' names PartnerUrl " +
                $"{EndpointUri.Sanitize(profile.PartnerUrl)}; the endpoint URI and the factory must name the same partner.");
        if (profile.MdnMode == As2MdnMode.Async && profile.AsyncMdnUrl is null)
            throw new InvalidOperationException(
                "AS2 producer: MdnMode is async, so AsyncMdnUrl must say where the partner posts the receipt.");
        _profile = profile;

        var handler = new HttpClientHandler();
        // Mutual TLS: present our certificate to the partner on HTTPS connections.
        if (profile.OurCertificate is not null)
            handler.ClientCertificates.Add(profile.OurCertificate);

        _http = new HttpClient(handler)
        {
            Timeout = _options.Timeout > 0 ? TimeSpan.FromMilliseconds(_options.Timeout) : Timeout.InfiniteTimeSpan,
        };
        return Task.CompletedTask;
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

        var profile = _profile!;
        // A pinned certificate is used only while valid: the partner would refuse the message anyway, after our retries.
        if (profile.Sign) As2CertificateValidity.Ensure(profile.OurCertificate!, "Our signing certificate");
        if (profile.Encrypt) As2CertificateValidity.Ensure(profile.PartnerCertificate!, "The partner's encryption certificate");

        // 1. Payload MIME entity from the exchange body.
        MimeEntity entity = BuildPayload(exchange.In);
        var uncompressed = entity;   // handle to the pre-compression payload, for the unsigned MIC

        // 2. compress → sign (compute MIC over what we sign) → encrypt, per profile.
        if (profile.Compress)
            entity = _engine.Compress(entity);

        As2Mic mic;
        if (profile.Sign)
        {
            // Signed: the MIC is over the signed content (the compressed part, when compress-then-sign) —
            // which is exactly what the receiver hashes from the signed entity.
            mic = _engine.ComputeMic(entity, profile.SignAlg, includeHeaders: true);
            entity = _engine.Sign(entity, profile.OurCertificate!, profile.SignAlg);
        }
        else
        {
            // Unsigned: the receiver decompresses first and hashes the DECOMPRESSED payload, so compute the MIC
            // over the uncompressed entity here — otherwise compress-without-sign always reported a MIC mismatch.
            mic = _engine.ComputeMic(uncompressed, profile.SignAlg, includeHeaders: false);
        }

        if (profile.Encrypt)
            entity = _engine.Encrypt(entity, profile.PartnerCertificate!, profile.EncryptAlg);

        // 3. Serialize; the top-level MIME headers become HTTP headers, the MIME body becomes the HTTP body.
        var (contentType, transferEncoding, contentDisposition, body) = SerializeEntity(entity);

        // 4. Assemble the AS2 HTTP request.
        var messageId = $"<{Guid.NewGuid():N}@redb.route>";
        var request = new HttpRequestMessage(HttpMethod.Post, profile.PartnerUrl);
        AddHeader(request, As2Headers.As2Version, "1.2");
        AddHeader(request, As2Headers.As2From, profile.As2From);
        AddHeader(request, As2Headers.As2To, profile.As2To);
        AddHeader(request, As2Headers.MessageId, messageId);
        AddHeader(request, As2Headers.Subject, exchange.In.GetHeader<string>(As2Headers.Subject) ?? "AS2 Message");
        if (profile.MdnMode != As2MdnMode.None)
        {
            AddHeader(request, As2Headers.DispositionNotificationTo,
                string.IsNullOrEmpty(profile.As2From) ? "as2@redb.route" : profile.As2From);
            if (profile.SignedMdn)
                AddHeader(request, As2Headers.DispositionNotificationOptions,
                    // Required, not optional: the agreement does not accept an unsigned receipt (MdnVerdict), so the
                    // partner must not think it may send one.
                    $"signed-receipt-protocol=required, pkcs7-signature; signed-receipt-micalg=required, {profile.SignAlg}");
            if (profile.MdnMode == As2MdnMode.Async && !string.IsNullOrEmpty(profile.AsyncMdnUrl))
                AddHeader(request, As2Headers.ReceiptDeliveryOption, profile.AsyncMdnUrl);
        }

        // Bridge non-reserved exchange headers (AS2 semantics + MIME headers are set explicitly, not bridged).
        BridgeHeaders(exchange.In, request);

        // Content + its MIME headers.
        var content = new ByteArrayContent(body);
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        if (!string.IsNullOrEmpty(transferEncoding))
            content.Headers.TryAddWithoutValidation("Content-Transfer-Encoding", transferEncoding);
        if (!string.IsNullOrEmpty(contentDisposition))
            content.Headers.TryAddWithoutValidation("Content-Disposition", contentDisposition);
        request.Content = content;

        // 5. Telemetry + POST. The span is opened on the core contract (EnableTelemetry=false opens none) and its context
        // is written to the request, replacing a traceparent bridged from the exchange: HttpClient writes none of its own
        // when the header is already there, and the partner would see the previous hop instead of this send.
        using var activity = RouteTelemetryExtensions.StartTransportSpan(
            _endpoint.Context, $"{profile.As2To} send", ActivityKind.Client, "messaging.system", "as2",
            _endpoint.Uri.NormalizedKey, destination: profile.PartnerUrl, operation: "send");
        RouteTelemetryExtensions.InjectTraceContext(activity, request, static (r, name, value) =>
        {
            r.Headers.Remove(name);
            r.Headers.TryAddWithoutValidation(name, value);
        });

        try
        {
            await SendAndVerifyAsync(exchange, profile, request, messageId, mic, ct).ConfigureAwait(false);
        }
        // A cancellation is a failure unless the caller asked for it: HttpClient reports its own timeout (no answer from
        // the partner within `timeout`) as TaskCanceledException.
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            activity.RecordFailure(e);
            throw;
        }
    }

    /// <summary>POSTs the message and, for a synchronous MDN, verifies the receipt; throws when the send failed.</summary>
    private async Task SendAndVerifyAsync(IExchange exchange, As2Profile profile, HttpRequestMessage request, string messageId,
        As2Mic mic, CancellationToken ct)
    {
        // Record what we send so the MDN can be correlated and its Received-Content-MIC verified.
        exchange.In.Headers[As2Headers.MessageId] = messageId;
        exchange.In.Headers[As2Headers.Mic] = mic.Digest;
        exchange.In.Headers[As2Headers.MicAlg] = mic.Algorithm;

        // Async: the wait exists BEFORE the POST. A partner may post the MDN before it answers this request (OpenAS2
        // often does); registered after the answer, that receipt would read as one nobody waits for. Nothing waits on
        // the entry: the verdict reaches the ReceiveMdn route as its own exchange.
        var correlation = profile.MdnMode == As2MdnMode.Async ? (_endpoint.Component as As2Component)?.Correlation : null;
        correlation?.Register(messageId, mic);

        HttpResponseMessage response;
        try
        {
            // Headers first: the body is read below, bounded, and only for a synchronous MDN.
            response = await _http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch
        {
            correlation?.Complete(messageId);   // not delivered: nothing is waiting for a receipt
            throw;
        }
        using var _ = response;

        if (!response.IsSuccessStatusCode)
        {
            correlation?.Complete(messageId);
            throw new HttpRequestException(
                $"AS2 POST to {EndpointUri.Sanitize(profile.PartnerUrl)} failed: {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        // Synchronous MDN: parse the receipt, verify its signature, and confirm the partner's
        // Received-Content-MIC matches what we sent. The MDN lands on exchange.Out (InOut).
        if (profile.MdnMode == As2MdnMode.Sync)
        {
            var mdnContentType = response.Content.Headers.ContentType?.ToString();
            var mdnCte = response.Content.Headers.TryGetValues("Content-Transfer-Encoding", out var cteValues)
                ? string.Join(",", cteValues) : null;
            var mdnBytes = await ReadBoundedAsync(response, messageId, ct).ConfigureAwait(false);

            var result = MdnParser.Parse(mdnContentType, mdnCte, mdnBytes, _engine, profile.PartnerCertificate);
            var verdict = MdnVerdict.Of(result, mic, profile.SignedMdn);

            var outMessage = new Message(mdnBytes) { ContentType = mdnContentType };
            outMessage.Headers[As2Headers.MdnDisposition] = result.Disposition ?? string.Empty;
            outMessage.Headers[As2Headers.SignatureValid] = result.SignatureValid;
            outMessage.Headers[As2Headers.MdnMicMatch] = verdict.MicMatch;
            outMessage.Headers[As2Headers.MdnMicStatus] = verdict.MicStatus;
            outMessage.Headers[As2Headers.MdnConfirmed] = verdict.Confirmed;
            exchange.Out = outMessage;
            exchange.Pattern = ExchangePattern.InOut;

            // Not confirmed: a negative disposition, a MIC that is not the one we sent (or is absent), or a missing or
            // invalid signature where a signed MDN is agreed. Always on the redbAs2.mdn* headers; with RequireValidMdn
            // also a failure of the send.
            if (verdict.Problem(result) is { } problem)
            {
                Logger?.LogWarning("AS2 MDN for message {MessageId} does not confirm the transfer: {Problem}.", messageId, problem);
                if (profile.RequireValidMdn)
                    throw new InvalidOperationException($"AS2 transfer of '{messageId}' is not confirmed by its MDN: {problem}.");
            }
        }

        Logger?.LogDebug("AS2 message sent: id={MessageId}, partner={Partner}", messageId, profile.As2To);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>The response body, refused past <c>maxResponseBodySize</c> without reading the rest (as the AS4 producer).</summary>
    private async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, string messageId, CancellationToken ct)
    {
        var limit = _options.MaxResponseBodySize;
        if (response.Content.Headers.ContentLength is { } declared && declared > limit)
            throw new InvalidOperationException(
                $"AS2 response to '{messageId}' declares {declared} bytes, over maxResponseBodySize {limit}; it is not an MDN.");

        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
                throw new InvalidOperationException(
                    $"AS2 response to '{messageId}' is over maxResponseBodySize {limit} bytes; it is not an MDN.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>Wraps the exchange body (byte[] preserved as-is; string/other UTF-8 encoded) in a MIME part.</summary>
    private static MimePart BuildPayload(IMessage message)
    {
        var bytes = message.Body switch
        {
            byte[] b => b,
            null => [],
            string s => Encoding.UTF8.GetBytes(s),
            _ => Encoding.UTF8.GetBytes(message.Body.ToString() ?? string.Empty),
        };

        var (mediaType, mediaSubtype) = SplitContentType(message.ContentType);
        return new MimePart(mediaType, mediaSubtype)
        {
            Content = new MimeContent(new MemoryStream(bytes, writable: false)),
            ContentTransferEncoding = ContentEncoding.Binary,
        };
    }

    private static (string mediaType, string mediaSubtype) SplitContentType(string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(contentType) && ContentType.TryParse(contentType, out var parsed))
            return (parsed.MediaType, parsed.MediaSubtype);
        return ("application", "octet-stream");
    }

    /// <summary>Serializes the entity (CRLF-canonical) and splits its top-level MIME headers from the body.</summary>
    private static (string contentType, string? transferEncoding, string? contentDisposition, byte[] body) SerializeEntity(MimeEntity entity)
    {
        using var ms = new MemoryStream();
        var options = FormatOptions.Default.Clone();
        options.NewLineFormat = NewLineFormat.Dos;
        entity.WriteTo(options, ms);
        var all = ms.ToArray();

        var sep = IndexOfDoubleCrlf(all);
        var body = sep >= 0 ? all[(sep + 4)..] : all;

        // Use the raw header VALUE — MimeKit's ContentType.ToString() renders the whole "Content-Type: ..."
        // header line (field name included), which would double the prefix on the wire.
        var contentType = entity.Headers[HeaderId.ContentType] ?? "application/octet-stream";
        var transferEncoding = entity.Headers[HeaderId.ContentTransferEncoding];
        var contentDisposition = entity.Headers[HeaderId.ContentDisposition];
        return (contentType, transferEncoding, contentDisposition, body);
    }

    private static int IndexOfDoubleCrlf(byte[] data)
    {
        for (var i = 0; i + 3 < data.Length; i++)
            if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                return i;
        return -1;
    }

    private static void AddHeader(HttpRequestMessage request, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            request.Headers.TryAddWithoutValidation(name, value);
    }

    /// <summary>
    /// Bridges the exchange's own headers onto the outgoing request, skipping redb metadata
    /// (<see cref="As2Headers.IsRedbHeader"/>) and the reserved set (MIME + AS2 semantics + hop-by-hop)
    /// that we set explicitly — so a passthrough can never silently corrupt them.
    /// </summary>
    private static void BridgeHeaders(IMessage message, HttpRequestMessage request)
    {
        foreach (var (key, value) in message.Headers)
        {
            if (value is null) continue;
            if (As2Headers.IsRedbHeader(key)) continue;
            if (As2Headers.NonBridgedHeaders.Contains(key)) continue;
            request.Headers.TryAddWithoutValidation(key, value.ToString());
        }
    }
}
