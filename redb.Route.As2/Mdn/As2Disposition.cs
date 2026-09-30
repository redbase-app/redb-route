namespace redb.Route.As2.Mdn;

/// <summary>
/// The disposition modifiers of RFC 4130 §7.4.3 a receiver reports when it did not process a message. The MDN carries
/// the code and a fixed text with a reference; what went wrong in detail stays in our log (the partner learns the
/// class of the failure, not our internals).
/// </summary>
internal static class As2Disposition
{
    /// <summary>The sender is not the agreed partner: identifiers or signature do not match the agreement.</summary>
    public const string AuthenticationFailed = "authentication-failed";

    /// <summary>The message could not be decrypted with our key.</summary>
    public const string DecryptionFailed = "decryption-failed";

    /// <summary>The compressed payload could not be decompressed.</summary>
    public const string DecompressionFailed = "decompression-failed";

    /// <summary>The message is signed or encrypted less than the agreement requires.</summary>
    public const string InsufficientMessageSecurity = "insufficient-message-security";

    /// <summary>Anything else: a malformed message, or the route failed to process it.</summary>
    public const string UnexpectedProcessingError = "unexpected-processing-error";

    /// <summary>The text of the MDN's human-readable part for a failure <paramref name="modifier"/>.</summary>
    public static string Describe(string modifier) => modifier switch
    {
        AuthenticationFailed => "The AS2 message could not be authenticated as coming from the agreed partner.",
        DecryptionFailed => "The AS2 message could not be decrypted.",
        DecompressionFailed => "The AS2 message could not be decompressed.",
        InsufficientMessageSecurity => "The AS2 message is not signed or encrypted as the agreement requires.",
        _ => "The AS2 message could not be processed.",
    };
}

/// <summary>A received message was not processed, for the reason <see cref="Modifier"/> names (RFC 4130 §7.4.3).</summary>
internal sealed class As2DispositionException : Exception
{
    public As2DispositionException(string modifier, string detail, Exception? inner = null)
        : base(detail, inner) => Modifier = modifier;

    /// <summary>One of the <see cref="As2Disposition"/> codes.</summary>
    public string Modifier { get; }
}
