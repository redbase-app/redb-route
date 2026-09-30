namespace redb.Route.As4.Security;

/// <summary>Namespaces and URIs of WS-Security, the SwA profile and XML Signature / Encryption used by AS4.</summary>
internal static class WsSecurityNames
{
    public const string Soap12 = "http://www.w3.org/2003/05/soap-envelope";
    public const string Wsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    public const string Wsse11 = "http://docs.oasis-open.org/wss/oasis-wss-wssecurity-secext-1.1.xsd";
    public const string Wsu = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";
    public const string Ds = "http://www.w3.org/2000/09/xmldsig#";
    public const string Xenc = "http://www.w3.org/2001/04/xmlenc#";
    public const string Xenc11 = "http://www.w3.org/2009/xmlenc11#";
    public const string Eb = "http://docs.oasis-open.org/ebxml-msg/ebms/v3.0/ns/core/200704/";

    /// <summary>X.509 v3 token type of a <c>wsse:BinarySecurityToken</c> and of a <c>wsse:Reference</c> to one.</summary>
    public const string X509V3 = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-x509-token-profile-1.0#X509v3";

    /// <summary>Value type of a <c>wsse:KeyIdentifier</c> carrying a Subject Key Identifier.</summary>
    public const string X509SubjectKeyIdentifier = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-x509-token-profile-1.0#X509SubjectKeyIdentifier";

    /// <summary>Base64 encoding type of token values.</summary>
    public const string Base64Binary = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary";

    /// <summary>SwA profile: signature over the content of a MIME part, headers excluded.</summary>
    public const string AttachmentContentSignatureTransform = "http://docs.oasis-open.org/wss/oasis-wss-SwAProfile-1.1#Attachment-Content-Signature-Transform";

    /// <summary>SwA profile: the ciphertext replaces the content of a MIME part.</summary>
    public const string AttachmentCiphertextTransform = "http://docs.oasis-open.org/wss/oasis-wss-SwAProfile-1.1#Attachment-Ciphertext-Transform";

    /// <summary>SwA profile <c>xenc:EncryptedData/@Type</c>: only the part content is encrypted.</summary>
    public const string AttachmentContentOnly = "http://docs.oasis-open.org/wss/oasis-wss-SwAProfile-1.1#Attachment-Content-Only";

    /// <summary>The <c>cid:</c> URI scheme of references to MIME parts.</summary>
    public const string CidPrefix = "cid:";

    /// <summary>Exclusive XML canonicalization without comments.</summary>
    public const string ExcC14N = "http://www.w3.org/2001/10/xml-exc-c14n#";

    /// <summary>The Content-ID a <c>cid:</c> URI names (RFC 2392 allows percent-encoding).</summary>
    public static string ContentId(string cidUri) => Uri.UnescapeDataString(cidUri[CidPrefix.Length..]);

    /// <summary>The <c>cid:</c> URI of a Content-ID.</summary>
    public static string CidUri(string contentId) => CidPrefix + contentId;
}
