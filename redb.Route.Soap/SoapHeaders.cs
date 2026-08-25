namespace redb.Route.Soap;

/// <summary>SOAP protocol version.</summary>
public enum SoapVersion
{
    /// <summary>SOAP 1.1: <c>text/xml</c> + a separate <c>SOAPAction</c> HTTP header.</summary>
    Soap11,
    /// <summary>SOAP 1.2: <c>application/soap+xml</c> with the action as a Content-Type parameter.</summary>
    Soap12
}

/// <summary>How the route sees the SOAP message (Apache Camel <c>camel-cxf</c> dataFormat parity).</summary>
public enum SoapDataFormat
{
    /// <summary>The XML of the <c>&lt;soap:Body&gt;</c> content. Default; works with any service, no codegen.</summary>
    Payload,
    /// <summary>The whole SOAP envelope (transparent proxy).</summary>
    Message,
    /// <summary>Typed request/response objects via <see cref="System.Xml.Serialization.XmlSerializer"/> (DTOs may be <c>dotnet-svcutil</c>-generated).</summary>
    Pojo
}

/// <summary>
/// Well-known exchange header keys for the SOAP component. Metadata with no standard wire name is
/// <c>redbSoap.</c>-prefixed and stripped on send (<see cref="IsRedbHeader"/>), mirroring
/// <c>RmqHeaders</c>/<c>HttpHeaders</c>. Envelope <c>&lt;soap:Header&gt;</c> elements map under
/// <see cref="HeaderPrefix"/> — a second header plane distinct from the transport HTTP headers.
/// </summary>
public static class SoapHeaders
{
    /// <summary>Prefix for redb-specific SOAP metadata headers.</summary>
    public const string Prefix = "redbSoap.";

    /// <summary>SOAPAction of the request/operation.</summary>
    public const string Action = "redbSoap.action";
    /// <summary>Operation name.</summary>
    public const string Operation = "redbSoap.operation";
    /// <summary>Target namespace.</summary>
    public const string Namespace = "redbSoap.namespace";
    /// <summary>Fault code (SOAP 1.1 <c>faultcode</c> / 1.2 <c>Code</c>).</summary>
    public const string FaultCode = "redbSoap.faultCode";
    /// <summary>Fault string (SOAP 1.1 <c>faultstring</c> / 1.2 <c>Reason</c>).</summary>
    public const string FaultString = "redbSoap.faultString";
    /// <summary>WS-Security UsernameToken user (surfaced on the consumer).</summary>
    public const string Username = "redbSoap.username";
    /// <summary>WS-Security UsernameToken password (surfaced on the consumer).</summary>
    public const string Password = "redbSoap.password";
    /// <summary>Whether an inbound WS-Security Body signature verified (bool).</summary>
    public const string SignatureValid = "redbSoap.signatureValid";

    /// <summary>Pojo mode: per-message override of the response CLR <see cref="System.Type"/> to deserialize into.</summary>
    public const string ResponseType = "redbSoap.responseType";

    /// <summary>MTOM/XOP attachments plane: an <see cref="IReadOnlyList{T}"/> of <see cref="SoapAttachment"/>.</summary>
    public const string Attachments = "redbSoap.attachments";
    /// <summary>Whether the message carries MTOM attachments (bool).</summary>
    public const string HasAttachments = "redbSoap.hasAttachments";

    /// <summary>Prefix for individual <c>&lt;soap:Header&gt;</c> block elements (the envelope header plane).</summary>
    public const string HeaderPrefix = "redbSoap.header.";

    /// <summary>Whether the header key is redb-internal SOAP metadata (stripped before the envelope goes out).</summary>
    public static bool IsRedbHeader(string key) => key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
}
