using System.Security.Cryptography.X509Certificates;
using System.Text;
using MimeKit;
using redb.Route.As2.Crypto;

namespace redb.Route.As2.Mdn;

/// <summary>
/// Builds an AS2 MDN (Message Disposition Notification) — a <c>multipart/report;
/// report-type=disposition-notification</c> with a human-readable part and a machine
/// <c>message/disposition-notification</c> part carrying <c>Original-Message-ID</c>, <c>Disposition</c>
/// and <c>Received-Content-MIC</c> (RFC 4130 §7), wrapped in <c>multipart/signed</c> when a signer is given.
/// </summary>
internal static class MdnBuilder
{
    /// <summary>
    /// Builds an MDN reporting on a message we received. <paramref name="ourAs2Id"/> is the receiver (us —
    /// the MDN's AS2-From and Final-Recipient); <paramref name="partnerAs2Id"/> is the original sender (AS2-To).
    /// Returns the MDN as HTTP-ready parts: the top-level Content-Type, the CTE, and the body bytes.
    /// <paramref name="failure"/> is null for a processed message, else an <see cref="As2Disposition"/> code; the
    /// human-readable part then carries the fixed text of the code and <paramref name="reference"/>, never our exception.
    /// <paramref name="warning"/> qualifies a processed message (RFC 4130 §7.4.3, e.g. <c>duplicate-document</c>).
    /// </summary>
    public static (string contentType, string? transferEncoding, byte[] body) Build(
        string originalMessageId,
        string ourAs2Id,
        string partnerAs2Id,
        As2Mic? receivedMic,
        string? failure = null,
        string? reference = null,
        IAs2CryptoEngine? signer = null,
        X509Certificate2? signerCert = null,
        string signAlg = "sha-256",
        string? warning = null)
    {
        var human = new TextPart("plain")
        {
            Text = failure is null
                ? "The AS2 message was received and processed successfully."
                : As2Disposition.Describe(failure) + (string.IsNullOrEmpty(reference) ? "" : $" Reference: {reference}."),
        };

        var machine = new SevenBitDispositionNotification();
        machine.Fields.Add("Reporting-UA", "redb.Route.As2");
        if (!string.IsNullOrEmpty(ourAs2Id)) machine.Fields.Add("Original-Recipient", $"rfc822; {ourAs2Id}");
        if (!string.IsNullOrEmpty(ourAs2Id)) machine.Fields.Add("Final-Recipient", $"rfc822; {ourAs2Id}");
        if (!string.IsNullOrEmpty(ourAs2Id)) machine.Fields.Add("AS2-From", ourAs2Id);
        if (!string.IsNullOrEmpty(partnerAs2Id)) machine.Fields.Add("AS2-To", partnerAs2Id);
        if (!string.IsNullOrEmpty(originalMessageId)) machine.Fields.Add("Original-Message-ID", originalMessageId);
        machine.Fields.Add("Disposition", failure is null
            ? "automatic-action/MDN-sent-automatically; processed" + (warning is null ? "" : $"/warning: {warning}")
            : $"automatic-action/MDN-sent-automatically; processed/error: {failure}");
        if (receivedMic is not null)
            machine.Fields.Add("Received-Content-MIC", receivedMic.Value.ToString());

        var report = new MultipartReport("disposition-notification") { human, machine };

        // A signed MDN wraps the report in multipart/signed (partners commonly require it).
        MimeEntity entity = signer is not null && signerCert is not null
            ? signer.Sign(report, signerCert, signAlg)
            : report;

        using var ms = new MemoryStream();
        var options = FormatOptions.Default.Clone();
        options.NewLineFormat = NewLineFormat.Dos;
        entity.WriteTo(options, ms);
        var all = ms.ToArray();

        var sep = IndexOfDoubleCrlf(all);
        var body = sep >= 0 ? all[(sep + 4)..] : all;
        var contentType = entity.Headers[HeaderId.ContentType] ?? "multipart/report";
        var transferEncoding = entity.Headers[HeaderId.ContentTransferEncoding];
        return (contentType, transferEncoding, body);
    }

    private static int IndexOfDoubleCrlf(byte[] data)
    {
        for (var i = 0; i + 3 < data.Length; i++)
            if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                return i;
        return -1;
    }
}

/// <summary>
/// The machine-readable MDN part, kept 7bit as RFC 3798 §3.1 requires ("MUST be used"). MimeKit's signing prepares
/// every part for a 78-character line and would re-encode a longer field line (an OpenAS2 Message-ID is about 90) as
/// quoted-printable; a partner that reads the fields without decoding them (OpenAS2 does) then cuts the
/// Original-Message-ID at the soft line break and never finds the message an asynchronous MDN reports on. The fields
/// are ASCII and far below the 998-character line limit of 7bit.
/// </summary>
internal sealed class SevenBitDispositionNotification : MessageDispositionNotification
{
    public SevenBitDispositionNotification() => ContentTransferEncoding = ContentEncoding.SevenBit;

    /// <inheritdoc />
    public override void Prepare(EncodingConstraint constraint, int maxLineLength = 78) =>
        ContentTransferEncoding = ContentEncoding.SevenBit;
}
