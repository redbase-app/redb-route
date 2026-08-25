using System.Security.Cryptography.X509Certificates;
using redb.Route.Core;

namespace redb.Route.Soap;

/// <summary>
/// A SOAP service binding: WSDL/endpoint, version, data format, and WS-Security material. Registered once by
/// name in the route registry and referenced by <c>.ConnectionFactory("name")</c>, so certificates and
/// credentials never live in a URI (same pattern as AS2 / RabbitMQ).
/// </summary>
public sealed class SoapConnectionFactory
{
    /// <summary>
    /// Optional WSDL: a file path or inline XML. A consumer publishes it on <c>GET ?wsdl</c> (address rewritten
    /// to the caller's URL). Not runtime-<em>imported</em> (.NET Core has no WSDL import) — it is the contract.
    /// </summary>
    public string? Wsdl { get; set; }

    /// <summary>Service endpoint URL to POST to (producer) or the public address (consumer).</summary>
    public string EndpointUrl { get; set; } = "";

    /// <summary>SOAP version. Drives Content-Type and SOAPAction placement.</summary>
    public SoapVersion SoapVersion { get; set; } = SoapVersion.Soap11;

    /// <summary>How the route sees the message. Default <see cref="SoapDataFormat.Payload"/>.</summary>
    public SoapDataFormat DataFormat { get; set; } = SoapDataFormat.Payload;

    /// <summary>Default SOAPAction when the exchange does not carry <c>redbSoap.action</c>.</summary>
    public string? DefaultAction { get; set; }

    // ── WS-Security (baseline: XML-Signature / XML-Encryption via System.Security.Cryptography.Xml) ──

    /// <summary>Our certificate with private key: signs outgoing, decrypts incoming.</summary>
    public X509Certificate2? SigningCert { get; set; }

    /// <summary>Partner's public certificate: encrypts outgoing, verifies their signature.</summary>
    public X509Certificate2? EncryptCert { get; set; }

    /// <summary>UsernameToken user.</summary>
    public string? Username { get; set; }

    /// <summary>UsernameToken password (redacted in logs and the dashboard).</summary>
    [Sensitive] public string? Password { get; set; }

    /// <summary>
    /// Use MTOM/XOP for binary attachments. When set, a producer with attachments on
    /// <see cref="SoapHeaders.Attachments"/> sends <c>multipart/related</c>; a consumer always parses inbound
    /// <c>multipart/related</c> regardless of this flag.
    /// </summary>
    public bool Mtom { get; set; }

    // ── Pojo dataFormat (typed serialization via XmlSerializer; DTOs may come from dotnet-svcutil) ──

    /// <summary>
    /// Pojo mode: the CLR type the inbound request body deserializes into on a consumer. XML-serializable
    /// (e.g. a <c>dotnet-svcutil</c>-generated DTO with <c>[XmlRoot]</c> matching the request element).
    /// </summary>
    public Type? RequestType { get; set; }

    /// <summary>
    /// Pojo mode: the CLR type the response body deserializes into on a producer (the reply object the route
    /// receives). Overridable per message via the <c>redbSoap.responseType</c> header.
    /// </summary>
    public Type? ResponseType { get; set; }

    /// <summary>Pojo mode only: a build-time <c>dotnet-svcutil</c> proxy type (optional).</summary>
    public Type? ProxyType { get; set; }
}
