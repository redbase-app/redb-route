using redb.Route.As2.Crypto;

namespace redb.Route.As2.Mdn;

/// <summary>
/// What a parsed MDN proves about one message, the same way on the synchronous and the asynchronous path. Only a
/// comparison that took place counts: a missing Received-Content-MIC, or a MIC nobody can compare because the message
/// is not known, is not a match (RFC 4130 §7.4.3 requires the field in every MDN).
/// </summary>
internal sealed record MdnVerdict(string MicStatus, bool SignatureAccepted, bool Confirmed)
{
    public const string Matched = "matched";
    public const string Mismatch = "mismatch";
    public const string Absent = "absent";
    public const string Unknown = "unknown";

    /// <summary>True when the MIC was compared and is the one we sent.</summary>
    public bool MicMatch => MicStatus == Matched;

    /// <param name="result">The parsed MDN.</param>
    /// <param name="sent">The MIC of the message the MDN reports on; null when that message is not known.</param>
    /// <param name="signedMdn">The agreement asks for a signed MDN.</param>
    public static MdnVerdict Of(MdnParser.MdnResult result, As2Mic? sent, bool signedMdn)
    {
        var status = sent is null ? Unknown
            : result.ReceivedMic is not { } received ? Absent
            : received.Matches(sent.Value) ? Matched
            : Mismatch;
        var signatureAccepted = !signedMdn || result.SignatureValid;
        return new MdnVerdict(status, signatureAccepted, result.IsPositive && status == Matched && signatureAccepted);
    }

    /// <summary>Why the MDN does not confirm the transfer, for logs and exceptions; null when it does.</summary>
    public string? Problem(MdnParser.MdnResult result) => Confirmed ? null : string.Join("; ", new[]
    {
        result.IsPositive ? null : $"disposition '{result.Disposition ?? "(none)"}'",
        MicMatch ? null : $"MIC {MicStatus}",
        SignatureAccepted ? null : "a signed MDN is agreed and its signature is missing or invalid",
    }.Where(p => p is not null));
}
