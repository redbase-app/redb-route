using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using redb.Route.Abstractions;
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
        var o = _endpoint.SoapOptions;
        // Publish the WSDL on GET ?wsdl when the factory carries one (camel-cxf parity); POST-only otherwise.
        var methods = string.IsNullOrWhiteSpace(ResolveFactory()?.Wsdl) ? "POST" : "POST,GET";
        _registration = _server.RegisterRoute(o.Host, o.Port, o.Path, methods, HandleRequest, o.UseTls);
        await _server.EnsureStarted(o.Host, o.Port, ct).ConfigureAwait(false);
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
                _endpoint.RecordError(ex);
                await WriteFault(http, version, $"Malformed MTOM request: {ex.Message}").ConfigureAwait(false);
                return;
            }
        }

        using var span = RouteTelemetryExtensions.StartTransportSpan(
            "soap receive", ActivityKind.Server, "rpc.system", "soap", _endpoint.Uri.NormalizedKey);
        _endpoint.RecordMessageIn();
        _endpoint.RecordBytesIn(wireLen);

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
                _endpoint.RecordError(ex);
                await WriteFault(http, version, $"Decryption failed: {ex.Message}").ConfigureAwait(false);
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
                _endpoint.RecordError(ex);
                await WriteFault(http, version, $"Malformed SOAP request: {ex.Message}").ConfigureAwait(false);
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

        var exchange = Exchange.Create(message, _endpoint.ScopeFactory);
        exchange.Pattern = ExchangePattern.InOut;

        try
        {
            try { await _processor.Process(exchange, http.RequestAborted).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { exchange.Exception ??= ex; }

            if (exchange.Exception is not null && !exchange.ExceptionHandled)
            {
                _endpoint.RecordError(exchange.Exception);
                await WriteFault(http, version, exchange.Exception.Message).ConfigureAwait(false);
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
        return (_endpoint.Component as ComponentBase)?.Context?.GetFromRegistry<SoapConnectionFactory>(name);
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

    private static async Task WriteFault(HttpContext http, SoapVersion version, string message)
    {
        var fault = SoapEnvelope.BuildFault(message, version);
        http.Response.StatusCode = version == SoapVersion.Soap12 ? 200 : 500; // 1.1 fault ⇒ HTTP 500 (SOAP convention)
        http.Response.ContentType = SoapEnvelope.ContentType(version, null);
        await http.Response.Body.WriteAsync(fault, http.RequestAborted).ConfigureAwait(false);
    }
}
