using System.Diagnostics;
using System.Net.Http.Headers;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.Soap;

/// <summary>
/// SOAP producer (baseline, in-box): wraps the exchange body in a 1.1/1.2 envelope, POSTs it with
/// <see cref="HttpClient"/>, and puts the response body on <see cref="IExchange.Out"/> (request-reply).
/// A <c>soap:Fault</c> surfaces on <c>redbSoap.fault*</c> and throws <see cref="SoapFaultException"/>.
/// Opens a <c>Client</c> telemetry span and records endpoint statistics, like every other connector.
/// </summary>
public sealed class SoapProducer : ConnectableProducer
{
    private readonly SoapEndpoint _endpoint;
    private HttpClient? _http;

    internal SoapProducer(SoapEndpoint endpoint)
        => _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));

    /// <inheritdoc />
    protected override IEndpoint ProducerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ProducerName => $"soap:{EndpointUri.Sanitize(_endpoint.Uri.NormalizedKey)}";

    /// <inheritdoc />
    protected override Task ConnectAsync(CancellationToken ct)
    {
        _http = new HttpClient();
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

        var factory = ResolveFactory();
        var version = factory?.SoapVersion ?? SoapVersion.Soap11;
        var url = ResolveUrl(factory);
        var dataFormat = factory?.DataFormat ?? SoapDataFormat.Payload;
        var action = exchange.In.GetHeader<string>(SoapHeaders.Action)
                     ?? _endpoint.SoapOptions.Action
                     ?? _endpoint.SoapOptions.Operation
                     ?? factory?.DefaultAction;

        var httpContentType = SoapEnvelope.ContentType(version, action);
        byte[] envelope;

        if (dataFormat == SoapDataFormat.Message)
        {
            // MESSAGE dataFormat (camel-cxf parity): transparent proxy — the route supplies the whole
            // envelope, so we neither wrap it nor inject headers/security; those are the route's own.
            envelope = exchange.In.Body is byte[] raw
                ? raw
                : System.Text.Encoding.UTF8.GetBytes(exchange.In.Body?.ToString() ?? string.Empty);
        }
        else
        {
            // PAYLOAD / POJO dataFormat: build an envelope around the body fragment.
            var bodyXml = dataFormat == SoapDataFormat.Pojo && exchange.In.Body is not null and not string
                ? SoapPojo.Serialize(exchange.In.Body)                    // typed object ⇒ document/literal XML
                : exchange.In.Body?.ToString() ?? string.Empty;

            var headerXml = BuildHeaderBlock(exchange.In.Headers);       // envelope <soap:Header> plane
            // WS-Security UsernameToken (Ф4a): prepend a <wsse:Security> header when the factory supplies creds.
            if (!string.IsNullOrEmpty(factory?.Username))
                headerXml = SoapSecurity.BuildUsernameTokenHeader(factory!.Username!, factory.Password) + (headerXml ?? string.Empty);
            envelope = SoapEnvelope.Build(bodyXml, version, headerXml);

            // WS-Security (Ф4b): sign with our cert, then encrypt to the partner's cert (sign-then-encrypt).
            if (factory?.SigningCert is not null || factory?.EncryptCert is not null)
            {
                var doc = new System.Xml.XmlDocument { PreserveWhitespace = true };
                doc.Load(new MemoryStream(envelope));
                if (factory.SigningCert is { } signingCert) SoapSignature.SignBody(doc, signingCert, version);
                if (factory.EncryptCert is { } encryptCert) SoapEncryption.EncryptBody(doc, encryptCert, version);
                using var protectedMs = new MemoryStream();
                doc.Save(protectedMs);
                envelope = protectedMs.ToArray();
            }
        }

        // MTOM/XOP (camel-cxf mtom-enabled parity): wrap envelope + attachments as multipart/related. For
        // SOAP 1.2 the action rides on the multipart Content-Type (1.1 keeps its SOAPAction header below).
        var attachments = exchange.In.GetHeader<IReadOnlyList<SoapAttachment>>(SoapHeaders.Attachments);
        if (factory?.Mtom == true && attachments is { Count: > 0 })
            (envelope, httpContentType) = SoapMultipart.Write(
                envelope, SoapEnvelope.ContentType(version, action), attachments,
                version == SoapVersion.Soap12 ? action : null);

        using var span = RouteTelemetryExtensions.StartTransportSpan(
            $"soap {action ?? "call"}", ActivityKind.Client, "rpc.system", "soap",
            _endpoint.Uri.NormalizedKey, url, action);

        _endpoint.RecordMessageOut();
        _endpoint.RecordBytesIn(envelope.Length);

        var content = new ByteArrayContent(envelope);
        if (MediaTypeHeaderValue.TryParse(httpContentType, out var mt))
            content.Headers.ContentType = mt;

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        if (version == SoapVersion.Soap11 && !string.IsNullOrEmpty(action))
            request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{action}\"");

        try
        {
            using var response = await _http!.SendAsync(request, ct).ConfigureAwait(false);
            var respBytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

            // Inbound MTOM: unwrap multipart/related and surface the attachments on the reply.
            IReadOnlyList<SoapAttachment>? respAttachments = null;
            if (SoapMultipart.IsMultipartRelated(response.Content.Headers.ContentType?.ToString()))
            {
                var (rootBytes, atts) = SoapMultipart.Parse(respBytes, response.Content.Headers.ContentType!.ToString());
                respBytes = rootBytes;
                respAttachments = atts;
            }

            // Symmetric to the consumer: decrypt an encrypted response when we hold the private key, so parse
            // and signature-verify below see plaintext. Skipped in Message (transparent proxy) mode — there
            // the route must receive the response bytes verbatim, exactly as the consumer skips inbound decrypt.
            if (dataFormat != SoapDataFormat.Message && factory?.SigningCert is { HasPrivateKey: true } decKey)
            {
                try
                {
                    var rdoc = new System.Xml.XmlDocument { PreserveWhitespace = true };
                    rdoc.Load(new MemoryStream(respBytes));
                    if (SoapEncryption.HasEncryptedData(rdoc))
                    {
                        SoapEncryption.DecryptBody(rdoc, decKey);
                        using var rms = new MemoryStream();
                        rdoc.Save(rms);
                        respBytes = rms.ToArray();
                    }
                }
                catch { /* leave as-is; parse below surfaces a fault if truly malformed */ }
            }

            var parsed = SoapEnvelope.Parse(respBytes, version);
            if (parsed.IsFault)
            {
                exchange.In.Headers[SoapHeaders.FaultCode] = parsed.FaultCode;
                exchange.In.Headers[SoapHeaders.FaultString] = parsed.FaultString;
                _endpoint.RecordError();
                throw new SoapFaultException(parsed.FaultCode, parsed.FaultString);
            }

            exchange.Out = dataFormat switch
            {
                // MESSAGE: hand back the whole envelope. POJO: deserialize into the response type. PAYLOAD: body XML.
                SoapDataFormat.Message => new Message(System.Text.Encoding.UTF8.GetString(respBytes))
                    { ContentType = SoapEnvelope.ContentType(version, null) },
                SoapDataFormat.Pojo when ResolveResponseType(exchange, factory) is { } rt
                    => new Message(SoapPojo.Deserialize(parsed.BodyXml, rt)) { ContentType = "text/xml" },
                _ => new Message(parsed.BodyXml) { ContentType = "text/xml" },
            };

            // Verify a response signature (authenticated when the partner cert is configured), like the consumer.
            // Not in Message mode: the route owns the verbatim envelope there.
            if (dataFormat != SoapDataFormat.Message)
            {
                try
                {
                    var rdoc = new System.Xml.XmlDocument { PreserveWhitespace = true };
                    rdoc.Load(new MemoryStream(respBytes));
                    if (SoapSignature.HasSignature(rdoc))
                        exchange.Out!.Headers[SoapHeaders.SignatureValid] = SoapSignature.VerifyBody(rdoc, factory?.EncryptCert, version);
                }
                catch { /* malformed signature — leave the flag unset */ }
            }

            if (respAttachments is { Count: > 0 })
            {
                exchange.Out!.Headers[SoapHeaders.Attachments] = respAttachments;
                exchange.Out!.Headers[SoapHeaders.HasAttachments] = true;
            }
        }
        catch (SoapFaultException) { throw; }
        catch (Exception ex)
        {
            _endpoint.RecordError(ex);
            throw;
        }
    }

    private static Type? ResolveResponseType(IExchange exchange, SoapConnectionFactory? factory)
        => exchange.In.GetHeader<Type>(SoapHeaders.ResponseType) ?? factory?.ResponseType;

    /// <summary>
    /// Builds the <c>&lt;soap:Header&gt;</c> content from every <c>redbSoap.header.*</c> exchange header (the
    /// envelope header plane). Other <c>redbSoap.*</c> metadata is deliberately NOT put on the wire.
    /// </summary>
    private static string? BuildHeaderBlock(IDictionary<string, object?> headers)
    {
        System.Text.StringBuilder? sb = null;
        foreach (var (key, value) in headers)
        {
            if (value is null || !key.StartsWith(SoapHeaders.HeaderPrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            (sb ??= new System.Text.StringBuilder()).Append(value);
        }
        return sb?.ToString();
    }

    private SoapConnectionFactory? ResolveFactory()
    {
        var name = _endpoint.SoapOptions.ConnectionFactory;
        if (string.IsNullOrEmpty(name)) return null;
        return (_endpoint.Component as ComponentBase)?.Context?.GetFromRegistry<SoapConnectionFactory>(name);
    }

    private string ResolveUrl(SoapConnectionFactory? factory)
    {
        if (!string.IsNullOrEmpty(factory?.EndpointUrl)) return factory!.EndpointUrl;
        // Reconstruct from the producer URI: soaps://host/path ⇒ https://host/path.
        var scheme = _endpoint.SoapOptions.UseTls ? "https" : "http";
        return $"{scheme}://{_endpoint.SoapOptions.Path}";
    }
}
