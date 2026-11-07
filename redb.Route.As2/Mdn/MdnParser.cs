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
            // A pinned certificate outside its validity verifies nothing (As2CertificateValidity).
            signatureValid = partnerCert is not null
                && As2CertificateValidity.Problem(partnerCert, DateTimeOffset.UtcNow, "The partner's MDN signing certificate") is null
                && engine.Verify(signed, partnerCert);
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

        return new MdnResult(originalMessageId, disposition, mic, signatureValid, IsPositiveDisposition(disposition));
    }

    /// <summary>
    /// Whether <paramref name="disposition"/> reports the message as processed, read by its fields (RFC 3798 §3.2.6,
    /// RFC 4130 §7.4.3): <c>action-mode/sending-mode; disposition-type[/modifier: text]</c>. Positive is the type
    /// <c>processed</c> with no modifier or a <c>warning</c> one; an <c>error</c> or <c>failure</c> modifier, any other
    /// type, or a value that is not in that form is not. The free text after the modifier is never read.
    /// </summary>
    public static bool IsPositiveDisposition(string? disposition)
    {
        if (string.IsNullOrWhiteSpace(disposition)) return false;
        var semicolon = disposition.IndexOf(';');
        if (semicolon < 0 || !disposition[..semicolon].Contains('/')) return false;

        var rest = disposition[(semicolon + 1)..].Trim();
        var slash = rest.IndexOf('/');
        var type = (slash < 0 ? rest : rest[..slash]).Trim();
        if (!type.Equals("processed", StringComparison.OrdinalIgnoreCase)) return false;
        if (slash < 0) return true;

        var modifier = rest[(slash + 1)..];
        var colon = modifier.IndexOf(':');
        return (colon < 0 ? modifier : modifier[..colon]).Trim().Equals("warning", StringComparison.OrdinalIgnoreCase);
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
