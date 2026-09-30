namespace redb.Route.As4;

/// <summary>
/// How a signature points at the signer's certificate (WS-Security X.509 Token Profile). eDelivery AS4
/// 1.16 asks implementations to support all three; <see cref="BinarySecurityToken"/> is the most widely
/// implemented. A receiver understands all three regardless of this setting: Holodeck B2B, for one,
/// defaults to <see cref="IssuerSerial"/>.
/// </summary>
public enum As4KeyReference
{
    /// <summary>The certificate travels in a <c>wsse:BinarySecurityToken</c> the signature references.</summary>
    BinarySecurityToken,

    /// <summary>The certificate is named by issuer and serial number; the receiver must already hold it.</summary>
    IssuerSerial,

    /// <summary>The certificate is named by its Subject Key Identifier; the receiver must already hold it.</summary>
    KeyIdentifier,
}

/// <summary>Whether the receive server asks the trading partner for a TLS client certificate.</summary>
public enum As4ClientCertificateMode
{
    /// <summary>No client certificate is requested (default).</summary>
    NoCertificate,

    /// <summary>A client certificate is requested but not required.</summary>
    AllowCertificate,

    /// <summary>A client certificate is required; the TLS handshake fails without one.</summary>
    RequireCertificate,
}
