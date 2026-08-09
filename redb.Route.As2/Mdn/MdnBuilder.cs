using System.Security.Cryptography.X509Certificates;
using System.Text;
using MimeKit;
using redb.Route.As2.Crypto;

namespace redb.Route.As2.Mdn;

/// <summary>
/// Builds an AS2 MDN (Message Disposition Notification) — a <c>multipart/report;
/// report-type=disposition-notification</c> with a human-readable part and a machine
/// <c>message/disposition-notification</c> part carrying <c>Original-Message-ID</c>, <c>Disposition</c>
/// and <c>Received-Content-MIC</c> (RFC 4130 §7). Ф3 produces an unsigned MDN; signing arrives in Ф4.
/// </summary>
internal static class MdnBuilder
{
    /// <summary>
    /// Builds an MDN reporting on a message we received. <paramref name="ourAs2Id"/> is the receiver (us —
    /// the MDN's AS2-From and Final-Recipient); <paramref name="partnerAs2Id"/> is the original sender (AS2-To).
    /// Returns the MDN as HTTP-ready parts: the top-level Content-Type, the CTE, and the body bytes.
    /// </summary>
    public static (string contentType, string? transferEncoding, byte[] body) Build(
        string originalMessageId,
        string ourAs2Id,
        string partnerAs2Id,
        As2Mic? receivedMic,
        bool success,
        string? errorText = null,
        IAs2CryptoEngine? signer = null,
        X509Certificate2? signerCert = null,
        string signAlg = "sha-256")
    {
        var human = new TextPart("plain")
        {
            Text = success
                ? "The AS2 message was received and processed successfully."
                : $"The AS2 message could not be processed: {errorText}",
        };

        var machine = new MessageDispositionNotification();
        machine.Fields.Add("Reporting-UA", "redb.Route.As2");
        if (!string.IsNullOrEmpty(ourAs2Id)) machine.Fields.Add("Original-Recipient", $"rfc822; {ourAs2Id}");
        if (!string.IsNullOrEmpty(ourAs2Id)) machine.Fields.Add("Final-Recipient", $"rfc822; {ourAs2Id}");
        if (!string.IsNullOrEmpty(ourAs2Id)) machine.Fields.Add("AS2-From", ourAs2Id);
        if (!string.IsNullOrEmpty(partnerAs2Id)) machine.Fields.Add("AS2-To", partnerAs2Id);
        if (!string.IsNullOrEmpty(originalMessageId)) machine.Fields.Add("Original-Message-ID", originalMessageId);
        machine.Fields.Add("Disposition", success
            ? "automatic-action/MDN-sent-automatically; processed"
            : $"automatic-action/MDN-sent-automatically; processed/error: {errorText}");
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
