using System.Security.Cryptography.X509Certificates;
using System.Text;
using MimeKit;
using MimeKit.Cryptography;
using redb.Route.As2.Crypto;

namespace redb.Route.As2.Mdn;

/// <summary>
/// Parses an inbound AS2 MDN: verifies its signature (if signed), and extracts the disposition and the
/// <c>Received-Content-MIC</c> so the sender can confirm the partner received exactly what was sent.
/// </summary>
internal static class MdnParser
{
    /// <summary>Outcome of parsing an MDN.</summary>
    public sealed record MdnResult(string? OriginalMessageId, string? Disposition, As2Mic? ReceivedMic, bool SignatureValid, bool IsPositive);

    public static MdnResult Parse(string? contentType, string? transferEncoding, byte[] body, IAs2CryptoEngine engine, X509Certificate2? partnerCert)
    {
        var entity = LoadEntity(contentType, transferEncoding, body);

        // Default false: an UNSIGNED MDN is not a valid signature. Only a present-and-verified signature sets it
        // true, so a sender that requires a signed receipt (SignedMdn) can reject a stripped/forged one.
        var signatureValid = false;
        if (entity is MultipartSigned signed)
        {
            signatureValid = partnerCert is not null && engine.Verify(signed, partnerCert);
            entity = signed[0];   // the multipart/report inside
        }

        string? originalMessageId = null;
        string? disposition = null;
        As2Mic? mic = null;

        if (entity is Multipart report)
        {
            foreach (var part in report)
            {
                if (part is not MessageDispositionNotification notification)
                    continue;

                originalMessageId = notification.Fields["Original-Message-ID"];
                disposition = notification.Fields["Disposition"];
                var micField = notification.Fields["Received-Content-MIC"];
                if (!string.IsNullOrEmpty(micField))
                {
                    try { mic = As2Mic.Parse(micField); }
                    catch (FormatException) { /* leave null on a malformed MIC */ }
                }
            }
        }

        var isPositive = disposition is not null
            && disposition.Contains("processed", StringComparison.OrdinalIgnoreCase)
            && !disposition.Contains("error", StringComparison.OrdinalIgnoreCase)
            && !disposition.Contains("failed", StringComparison.OrdinalIgnoreCase);

        return new MdnResult(originalMessageId, disposition, mic, signatureValid, isPositive);
    }

    private static MimeEntity LoadEntity(string? contentType, string? transferEncoding, byte[] body)
    {
        var sb = new StringBuilder();
        sb.Append("Content-Type: ").Append(contentType ?? "multipart/report").Append("\r\n");
        if (!string.IsNullOrEmpty(transferEncoding))
            sb.Append("Content-Transfer-Encoding: ").Append(transferEncoding).Append("\r\n");
        sb.Append("\r\n");

        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes(sb.ToString()));
        stream.Write(body);
        stream.Position = 0;
        return MimeEntity.Load(stream);
    }
}
