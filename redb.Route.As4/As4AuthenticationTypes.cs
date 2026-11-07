namespace redb.Route.As4;

/// <summary>
/// Authentication types of the identities an AS4 receiver puts on the exchange principal (<c>ExchangePrincipal</c>): the
/// envelope signer first, then the TLS client certificate when the partner presented one (docs/as4/09 No. 10).
/// </summary>
public static class As4AuthenticationTypes
{
    /// <summary>
    /// The identity proven by the envelope signature. Claims: <c>NameIdentifier</c> — the partner (agreement) the message
    /// was matched to; <c>Name</c> — the signing certificate's subject; <c>Thumbprint</c> — its thumbprint.
    /// </summary>
    public const string Signature = "AS4-Signature";

    /// <summary>The identity proven by a TLS client certificate. Claims: <c>Name</c> — its subject; <c>Thumbprint</c>.</summary>
    public const string ClientCertificate = "TLS-ClientCertificate";
}
