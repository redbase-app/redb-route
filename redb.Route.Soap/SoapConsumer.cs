using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Extensions;
using redb.Route.Core;
using redb.Route.Http;
using redb.Route.Telemetry;

namespace redb.Route.Soap;

/// <summary>
/// SOAP consumer (baseline, in-box): hosts a SOAP receive endpoint on the shared Kestrel server. Parses the
/// inbound envelope (version detected from Content-Type), delivers the <c>&lt;soap:Body&gt;</c> payload to
/// the route, and returns a response envelope built from <see cref="IExchange.Out"/> (request-reply). A route
/// exception becomes a <c>soap:Fault</c> response. Records <c>MessagesIn</c> and opens a <c>Server</c> span.
/// </summary>
public sealed class SoapConsumer : IConsumer
{
    private readonly SoapEndpoint _endpoint;
    private readonly IProcessor _processor;
    private readonly SharedHttpServerManager _server;
    private RouteRegistration? _registration;
    private ILogger? _logger;

    public IEndpoint Endpoint => _endpoint;

    internal SoapConsumer(SoapEndpoint endpoint, IProcessor processor, SharedHttpServerManager server)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _server = server ?? throw new ArgumentNullException(nameof(server));
    }

    /// <inheritdoc />
    public async Task Start(CancellationToken ct = default)
    {
        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        var o = _endpoint.SoapOptions;
        var factory = ResolveFactory();

        // Publish the WSDL on GET ?wsdl when the factory carries one (camel-cxf parity); POST-only otherwise.
        var methods = string.IsNullOrWhiteSpace(factory?.Wsdl) ? "POST" : "POST,GET";

        // TLS material: the URI wins where it says something, the named factory fills the rest. That is
        // the point of the factory — a certificate password put in a URI travels into logs, telemetry and
        // the dashboard, because the URI is the route key and gets handled as an ordinary string.
        var ssl = o.UseTls || (factory?.Ssl ?? false);
        var certPath = o.SslCertPath ?? factory?.SslCertPath;
        var certPassword = o.SslCertPassword ?? factory?.SslCertPassword;

        var certMode = o.ClientCertificateMode != SoapClientCertificateMode.NoCertificate
            ? o.ClientCertificateMode
            : factory?.ClientCertificateMode ?? SoapClientCertificateMode.NoCertificate;

        var thumbprints = o.AllowedClientThumbprints ?? factory?.AllowedClientThumbprints;

        // Fail here rather than inside Kestrel: without a certificate the listener falls back to the
        // ASP.NET development certificate, which is present on a developer's machine and absent in
        // production. That turns a configuration mistake into either a service that silently serves a
        // certificate nobody trusts, or one that fails on a box with an error naming neither this route
        // nor TLS.
        if (ssl && string.IsNullOrWhiteSpace(certPath))
            throw new InvalidOperationException(
                $"SOAP consumer on {o.Host}:{o.Port}{o.Path} is set to serve TLS but has no certificate. " +
                "Set sslCertPath on the endpoint URI, or SslCertPath on the connection factory it names.");

        if (certMode != SoapClientCertificateMode.NoCertificate && !ssl)
            throw new InvalidOperationException(
                $"SOAP consumer on {o.Host}:{o.Port}{o.Path} asks for client certificates without TLS. " +
                "A client certificate is presented during the TLS handshake, so there is nowhere to " +
                "present it — use the soaps scheme, or drop clientCertificateMode.");

        _registration = _server.RegisterRoute(
            o.Host, o.Port, o.Path, methods, HandleRequest,
            ssl, certPath, certPassword,
            corsOptions: null,                 // SOAP is not a browser transport
            maxRequestBodySize: 0,             // unchanged: the connector caps nothing itself
            clientCertificateMode: MapClientCertificateMode(certMode),
            clientCertificateValidation: BuildThumbprintValidator(thumbprints),
            concurrencyLimit: ConcurrencyLimitOptions.FromEndpoint(
                o.MaxConcurrentRequests, o.RequestQueueLimit,
                o.RejectStatusCode, o.RetryAfterSeconds,
                onRejected: _endpoint.RecordRejected));

        await _server.EnsureStarted(o.Host, o.Port, ct).ConfigureAwait(false);
    }

    private static Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode? MapClientCertificateMode(
        SoapClientCertificateMode mode) => mode switch
    {
        SoapClientCertificateMode.AllowCertificate =>
            Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.AllowCertificate,
        SoapClientCertificateMode.RequireCertificate =>
            Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.RequireCertificate,
        _ => null,
    };

    /// <summary>
    /// Builds the allow-list check for client certificates. Chain validation is Kestrel's job; this adds
    /// "and it must be one of ours", which is what pins a partner in a service-to-service contour.
    /// </summary>
    private static Func<System.Security.Cryptography.X509Certificates.X509Certificate2,
        System.Security.Cryptography.X509Certificates.X509Chain?,
        System.Net.Security.SslPolicyErrors, bool>? BuildThumbprintValidator(string? allowedThumbprints)
    {
        if (string.IsNullOrWhiteSpace(allowedThumbprints)) return null;

        var allowed = allowedThumbprints!
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Replace(" ", string.Empty))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return (cert, _, _) => allowed.Contains(cert.Thumbprint);
    }

    /// <inheritdoc />
    public async Task Stop(CancellationToken ct = default)
    {
        if (_registration is null) return;
        _server.UnregisterRoute(_registration);
        _registration = null;
        await _server.StopIfEmpty(_endpoint.SoapOptions.Host, _endpoint.SoapOptions.Port, ct).ConfigureAwait(false);
    }

    private async Task HandleRequest(HttpContext http)
    {
        // WSDL publishing: a GET serves the contract and never enters the SOAP pipeline.
        if (HttpMethods.IsGet(http.Request.Method))
        {
            await ServeWsdl(http).ConfigureAwait(false);
            return;
        }

        byte[] reqBytes;
        using (var ms = new MemoryStream())
        {
            await http.Request.Body.CopyToAsync(ms, http.RequestAborted).ConfigureAwait(false);
            reqBytes = ms.ToArray();
        }
        var wireLen = reqBytes.Length;

        var factory = ResolveFactory();
        var dataFormat = factory?.DataFormat ?? SoapDataFormat.Payload;
        var contentType = http.Request.ContentType;

        // Version up front — from the multipart start-info when MTOM — so an MTOM parse failure faults in the
        // right SOAP dialect rather than always defaulting to 1.1.
        var version = DetectVersion(SoapMultipart.IsMultipartRelated(contentType)
            ? SoapMultipart.StartInfo(contentType!) ?? "text/xml"
            : contentType);

        // Inbound MTOM/XOP (camel-cxf mtom-enabled parity): unwrap multipart/related into the root envelope
        // plus attachments, then continue as if a plain envelope had arrived.
        IReadOnlyList<SoapAttachment>? inAttachments = null;
        if (SoapMultipart.IsMultipartRelated(contentType))
        {
            try
            {
                var (rootBytes, atts) = SoapMultipart.Parse(reqBytes, contentType!);
                reqBytes = rootBytes;
                inAttachments = atts;
            }
            catch (Exception ex)
            {
                // The text stays: SoapMultipart throws our own messages about the caller's own multipart
                // framing, so it discloses nothing about this process and is the only thing that lets an
                // integrator find a missing boundary. What changes is the code — see WriteFault below.
                _endpoint.RecordError(ex);
                await WriteFault(http, version, $"Malformed MTOM request: {ex.Message}",
                    SoapEnvelope.SenderCode(version)).ConfigureAwait(false);
                return;
            }
        }

        using var span = RouteTelemetryExtensions.StartTransportSpan(
            "soap receive", ActivityKind.Server, "rpc.system", "soap", _endpoint.Uri.NormalizedKey);
        // Pipeline statistics (MessagesIn/BytesIn) are the core StatisticsProcessor's - the
        // ownership audit removed the double. Transport-level failures above stay recorded here.

        // WS-Security decrypt (Ф4b): if the Body is encrypted and we hold the private key, decrypt first,
        // so signature verification and parsing below see the plaintext. Skipped in Message (transparent
        // proxy) mode — there the route receives the wire bytes verbatim.
        if (dataFormat != SoapDataFormat.Message && factory?.SigningCert is { HasPrivateKey: true } decKey)
        {
            try
            {
                var doc = new System.Xml.XmlDocument { PreserveWhitespace = true };
                doc.Load(new MemoryStream(reqBytes));
                if (SoapEncryption.HasEncryptedData(doc))
                {
                    SoapEncryption.DecryptBody(doc, decKey);
                    using var dms = new MemoryStream();
                    doc.Save(dms);
                    reqBytes = dms.ToArray();
                }
            }
            catch (Exception ex)
            {
                // This one is different from the two parse failures around it, and the difference is not
                // style. CryptographicException describes OUR key and OUR configuration, not the caller's
                // bytes; and text that varies with the reason decryption failed is a decryption oracle.
                // WS-Security says so outright — a fault here "could be used as part of a denial of
                // service or cryptographic attack" — and gives exactly one code for every cause,
                // wsse:FailedCheck ("the signature or decryption was invalid"). One code, one sentence,
                // no gradations: an attacker learns nothing by varying the ciphertext. The exception goes
                // to the log, and the caller gets the request id to quote at us.
                _logger?.LogError(ex, "SOAP decryption failed for {Path} (request {RequestId})",
                    http.Request.Path, http.TraceIdentifier);
                _endpoint.RecordError(ex);
                await WriteFault(http, version,
                    $"Security check failed (ref: {http.TraceIdentifier}).",
                    "wsse:FailedCheck").ConfigureAwait(false);
                return;
            }
        }

        SoapParseResult parsed;
        try { parsed = SoapEnvelope.Parse(reqBytes, version); }
        catch (Exception ex)
        {
            // Message mode is a transparent proxy: hand the raw envelope to the route even if it has no
            // parseable <Body>, rather than faulting on the route's behalf.
            if (dataFormat == SoapDataFormat.Message)
                parsed = new SoapParseResult(false, null, null, string.Empty);
            else
            {
                // XmlException here reads "'<' is an unexpected token. Line 1, position 5." — a description
                // of the bytes the caller sent us, not of anything on this side. Stripping it would cost
                // an integrator the one clue that finds a BOM, a wrong encoding or a truncated stream,
                // and buy no secrecy: we would only be refusing to quote the caller their own input.
                _endpoint.RecordError(ex);
                await WriteFault(http, version, $"Malformed SOAP request: {ex.Message}",
                    SoapEnvelope.SenderCode(version)).ConfigureAwait(false);
                return;
            }
        }

        // The message the route sees depends on dataFormat: MESSAGE = whole envelope, POJO = a deserialized
        // request object, PAYLOAD = the inner <soap:Body> payload. The wrapper Content-Type is NOT leaked.
        object? bodyForRoute = dataFormat switch
        {
            SoapDataFormat.Message => System.Text.Encoding.UTF8.GetString(reqBytes),
            SoapDataFormat.Pojo when factory?.RequestType is { } reqType => SoapPojo.Deserialize(parsed.BodyXml, reqType),
            _ => parsed.BodyXml,
        };
        var message = new Message(bodyForRoute) { ContentType = "text/xml" };
        if (inAttachments is { Count: > 0 })
        {
            message.Headers[SoapHeaders.Attachments] = inAttachments;
            message.Headers[SoapHeaders.HasAttachments] = true;
        }
        var action = ExtractAction(http.Request, version);
        if (!string.IsNullOrEmpty(action)) message.Headers[SoapHeaders.Action] = action;

        // WS-Security Body signature (Ф4b): verify when present, using the cert embedded in the signature.
        try
        {
            var doc = new System.Xml.XmlDocument { PreserveWhitespace = true };
            doc.Load(new MemoryStream(reqBytes));
            // EncryptCert is the partner's known cert; when set, verification also authenticates the signer.
            if (SoapSignature.HasSignature(doc))
                message.Headers[SoapHeaders.SignatureValid] = SoapSignature.VerifyBody(doc, factory?.EncryptCert, version);
        }
        catch { /* malformed signature — leave the flag unset */ }

        // Operation = local name of the Body payload's root element (so routes can branch on it).
        if (!string.IsNullOrWhiteSpace(parsed.BodyXml))
        {
            try { message.Headers[SoapHeaders.Operation] = System.Xml.Linq.XElement.Parse(parsed.BodyXml).Name.LocalName; }
            catch { /* non-element body — no operation */ }
        }

        // Second header plane: envelope <soap:Header> elements under redbSoap.header.*
        // Guarded: in Message mode the body may be non-XML (tolerated above), and ReadHeaders re-parses it.
        try
        {
            foreach (var h in SoapEnvelope.ReadHeaders(reqBytes, version))
            {
                message.Headers[SoapHeaders.HeaderPrefix + h.Name.LocalName] = h.ToString();

                // WS-Security UsernameToken → surface user/password for the route to authenticate.
                if (SoapSecurity.IsSecurityHeader(h) && SoapSecurity.ReadUsernameToken(h) is { } creds)
                {
                    message.Headers[SoapHeaders.Username] = creds.Username;
                    if (creds.Password is not null) message.Headers[SoapHeaders.Password] = creds.Password;
                }
            }
        }
        catch { /* non-XML / headerless body — no envelope header plane */ }

        SurfaceConnection(http, message, _endpoint.SoapOptions.EmitHttpCompatHeaders);

        var exchange = Exchange.Create(message, _endpoint.ScopeFactory);
        exchange.Pattern = ExchangePattern.InOut;

        try
        {
            try { await _processor.Process(exchange, http.RequestAborted).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { exchange.Exception ??= ex; }

            if (exchange.Exception is not null && !exchange.ExceptionHandled)
            {
                // No RecordError: the core StatisticsProcessor saw the same exchange.Exception.

                // A route that threw SoapFaultException has already said what kind of failure this is.
                // Flattening it to soap:Server would leave the caller only prose to branch on, and prose
                // is not a contract: WS-Trust clients, for one, treat wst:FailedAuthentication and
                // wst:InvalidRequest as different outcomes.
                // FaultString, not Message: Exception.Message on a SoapFaultException is the composed
                // "SOAP fault: <code> - <reason>" text, so using it would send the caller our rendering
                // of a fault instead of the reason the route actually gave.
                // Anything that is NOT a SoapFaultException is an unhandled failure, and its Message is
                // written by whoever threw it — a file path, a connection string, the name of an inner
                // service. That text never goes to the caller (BR-4); the fault carries the exchange id
                // to quote, and the core StatisticsProcessor keeps the exception for diagnostics.
                // A request that could not be BOUND is the caller's fault in SOAP's literal sense:
                // MalformedRequestException carries a message about the caller's own bytes (the
                // BR-4 rule allows exactly that class of text out), and the code must be Sender —
                // a Receiver fault is a retry hint, and these bytes will fail identically forever.
                if (exchange.Exception is MalformedRequestException malformed)
                {
                    await WriteFault(http, version, malformed.Message, SoapEnvelope.SenderCode(version))
                        .ConfigureAwait(false);
                    return;
                }

                var chosen = exchange.Exception as SoapFaultException;
                await WriteFault(
                        http, version,
                        chosen?.FaultString
                            ?? $"An unexpected error occurred while processing the request (ref: {exchange.ExchangeId}).",
                        chosen?.FaultCode,
                        chosen?.FaultCodeNamespace)
                    .ConfigureAwait(false);
                return;
            }

            var outMsg = exchange.HasOut ? exchange.Out! : exchange.In;
            byte[] respEnvelope;
            var respContentType = SoapEnvelope.ContentType(version, null);

            if (dataFormat == SoapDataFormat.Message)
            {
                // Transparent proxy: the route's reply is already a whole envelope — send it verbatim.
                respEnvelope = outMsg.Body is byte[] rb
                    ? rb
                    : System.Text.Encoding.UTF8.GetBytes(outMsg.Body?.ToString() ?? string.Empty);
            }
            else
            {
                // A byte[] reply is already-serialized XML (UTF-8), not an object to ToString(); decode it
                // rather than emitting "System.Byte[]". Pojo mode serializes a typed object; otherwise ToString().
                var respBodyXml = outMsg.Body switch
                {
                    byte[] b => System.Text.Encoding.UTF8.GetString(b),
                    string s => s,
                    null => string.Empty,
                    var o when dataFormat == SoapDataFormat.Pojo => SoapPojo.Serialize(o),
                    var o => o.ToString() ?? string.Empty,
                };
                respEnvelope = SoapEnvelope.Build(respBodyXml, version);

                // WS-Security on the response, symmetric to the producer's request leg (sign-then-encrypt), so
                // the caller's decrypt/verify has something to act on. Only in Payload/Pojo (Message is verbatim).
                if (factory?.SigningCert is not null || factory?.EncryptCert is not null)
                {
                    var sdoc = new System.Xml.XmlDocument { PreserveWhitespace = true };
                    sdoc.Load(new MemoryStream(respEnvelope));
                    if (factory.SigningCert is { } signCert) SoapSignature.SignBody(sdoc, signCert, version);
                    if (factory.EncryptCert is { } encCert) SoapEncryption.EncryptBody(sdoc, encCert, version);
                    using var sms = new MemoryStream();
                    sdoc.Save(sms);
                    respEnvelope = sms.ToArray();
                }
            }

            // Outbound MTOM: package the reply with attachments the route explicitly set. The guard against
            // the inbound list prevents silently echoing the caller's own attachments back when a route
            // mutates In in place and never sets a distinct reply list.
            var outAttachments = outMsg.GetHeader<IReadOnlyList<SoapAttachment>>(SoapHeaders.Attachments);
            if (factory?.Mtom == true && outAttachments is { Count: > 0 } && !ReferenceEquals(outAttachments, inAttachments))
                (respEnvelope, respContentType) = SoapMultipart.Write(respEnvelope, SoapEnvelope.ContentType(version, null), outAttachments);

            http.Response.ContentType = respContentType;
            await http.Response.Body.WriteAsync(respEnvelope, http.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            await exchange.DisposeAsync().ConfigureAwait(false);
        }
    }

    private SoapConnectionFactory? ResolveFactory()
    {
        var name = _endpoint.SoapOptions.ConnectionFactory;
        if (string.IsNullOrEmpty(name)) return null;
        // A set-but-unknown name fails loud -- never a silent fallback (Ф11 Ж-1).
        return ((_endpoint.Component as ComponentBase)?.Context).GetRequiredFromRegistry<SoapConnectionFactory>(name);
    }

    /// <summary>
    /// Surfaces what the connection itself says: who called, and what the handshake proved about them.
    /// <para>
    /// A route had no way to learn any of this before — not the caller's address, not their certificate.
    /// The address is what every IP-keyed protection keys on (rate limiting, brute-force lockout, audit
    /// of where a request came from), and its absence did not make those protections fail: they saw
    /// nothing and quietly did nothing, which is the worse of the two outcomes.
    /// </para>
    /// </summary>
    private static void SurfaceConnection(HttpContext http, Message message, bool emitHttpCompat)
    {
        var remote = http.Connection.RemoteIpAddress;
        if (remote is not null)
        {
            var ip = remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4().ToString() : remote.ToString();
            message.Headers[SoapHeaders.RemoteAddress] = ip;
            message.Headers[SoapHeaders.RemotePort] = http.Connection.RemotePort;

            // Opt-in bridge for processors written against the HTTP transport, the same one the gRPC
            // consumer offers. Off by default: writing into another transport's namespace is a
            // deliberate act, not a side effect of choosing SOAP.
            if (emitHttpCompat)
                message.Headers["redbHttp.RemoteAddress"] = ip;
        }

        if (emitHttpCompat)
        {
            message.Headers["redbHttp.Path"] = http.Request.Path.Value ?? string.Empty;
            message.Headers["redbHttp.Method"] = http.Request.Method;
        }

        // mTLS: what the handshake proved about the caller. Chain and allow-list checks already ran in
        // Kestrel, so a certificate reaching here is one the listener accepted.
        var clientCert = http.Connection.ClientCertificate;
        if (clientCert is not null)
        {
            message.Headers[SoapHeaders.ClientCertThumbprint] = clientCert.Thumbprint;
            message.Headers[SoapHeaders.ClientCertSubject] = clientCert.Subject;
            message.Headers[SoapHeaders.ClientCertNotAfter] = clientCert.NotAfter.ToUniversalTime().ToString("O");
        }
    }

    /// <summary>Serves the configured WSDL (address rewritten to the caller's URL), or 404 when none is set.</summary>
    private async Task ServeWsdl(HttpContext http)
    {
        var xml = SoapWsdl.Load(ResolveFactory()?.Wsdl);
        if (xml is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var endpointUrl = $"{http.Request.Scheme}://{http.Request.Host}{http.Request.Path}";
        http.Response.ContentType = "text/xml; charset=utf-8";
        await http.Response.WriteAsync(SoapWsdl.RewriteAddress(xml, endpointUrl), http.RequestAborted).ConfigureAwait(false);
    }

    private static SoapVersion DetectVersion(string? contentType) =>
        contentType is not null && contentType.Contains("application/soap+xml", StringComparison.OrdinalIgnoreCase)
            ? SoapVersion.Soap12
            : SoapVersion.Soap11;

    private static string? ExtractAction(HttpRequest request, SoapVersion version)
    {
        if (version == SoapVersion.Soap11)
            return request.Headers.TryGetValue("SOAPAction", out var v) ? v.ToString().Trim('"') : null;

        // 1.2: action is a Content-Type parameter. Take the value up to the next ';' so it parses correctly
        // even when action is not the last parameter (e.g. "…; action=\"X\"; charset=utf-8").
        var ct = request.ContentType ?? string.Empty;
        var idx = ct.IndexOf("action=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var rest = ct[(idx + "action=".Length)..];
        var end = rest.IndexOf(';');
        return (end < 0 ? rest : rest[..end]).Trim().Trim('"');
    }

    private static async Task WriteFault(
        HttpContext http, SoapVersion version, string message, string? faultCode = null,
        string? faultCodeNamespace = null)
    {
        var fault = SoapEnvelope.BuildFault(message, version, faultCode, faultCodeNamespace);
        http.Response.StatusCode = version == SoapVersion.Soap12 ? 200 : 500; // 1.1 fault ⇒ HTTP 500 (SOAP convention)
        http.Response.ContentType = SoapEnvelope.ContentType(version, null);
        await http.Response.Body.WriteAsync(fault, http.RequestAborted).ConfigureAwait(false);
    }
}
