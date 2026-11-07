using Microsoft.AspNetCore.Http;
using redb.Route.As2.Crypto;

namespace redb.Route.As2.Mdn;

/// <summary>
/// The receipt a received message asks for (RFC 4130 §7.3): whether any (<c>Disposition-Notification-To</c>), whether
/// signed and with which MIC algorithm (<c>Disposition-Notification-Options</c>:
/// <c>signed-receipt-protocol=importance, pkcs7-signature; signed-receipt-micalg=importance, alg[, alg...]</c>).
/// </summary>
/// <param name="Requested">The sender asked for an MDN.</param>
/// <param name="Signed">The sender asked for a signed MDN (<c>pkcs7-signature</c>).</param>
/// <param name="MicAlg">The algorithm of the MIC and of the MDN signature: the first requested one usable here, else the agreement's.</param>
/// <param name="RequestedMicAlgs">The algorithms the sender listed, in its order; empty when it listed none.</param>
internal sealed record ReceiptRequest(bool Requested, bool Signed, string MicAlg, IReadOnlyList<string> RequestedMicAlgs)
{
    public static ReceiptRequest Of(HttpRequest request, As2Profile profile)
    {
        var requested = !string.IsNullOrWhiteSpace(request.Headers[As2Headers.DispositionNotificationTo].ToString());
        var options = request.Headers[As2Headers.DispositionNotificationOptions].ToString();

        var signed = false;
        IReadOnlyList<string> algs = [];
        foreach (var parameter in options.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = parameter.IndexOf('=');
            if (eq < 0) continue;
            var name = parameter[..eq].Trim();
            // The first value is the importance (required | optional); the rest are the values.
            var values = parameter[(eq + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1).ToList();
            if (name.Equals("signed-receipt-protocol", StringComparison.OrdinalIgnoreCase))
                signed = values.Any(v => v.Equals("pkcs7-signature", StringComparison.OrdinalIgnoreCase));
            else if (name.Equals("signed-receipt-micalg", StringComparison.OrdinalIgnoreCase))
                algs = values;
        }

        var micAlg = algs.FirstOrDefault(a => As2CryptoEngine.IsSupportedDigest(a) && (!As2Profile.IsLegacy(a) || profile.AllowLegacyAlgorithms))
            ?? profile.SignAlg;
        return new ReceiptRequest(requested, signed, micAlg, algs);
    }
}
