using redb.Route.As4.Messaging;

namespace redb.Route.As4.Mime;

/// <summary>
/// The payload references of a user message and the MIME parts of its message must match one to one
/// (ebMS 3.0 Core §5.2.2.13, AS4 profile): every <c>cid:</c> reference names a part, every part is referenced.
/// A reference outside the message is not supported.
/// </summary>
internal static class PayloadConsistency
{
    /// <summary>Upper bound on attachments per message; beyond it the message is refused before any crypto work.</summary>
    public const int DefaultMaxParts = 100;

    /// <summary>
    /// Pairs each <c>eb:PartInfo</c> of <paramref name="message"/> with its part, or throws
    /// <see cref="As4ErrorCode.MimeInconsistency"/> / <see cref="As4ErrorCode.ExternalPayloadError"/>.
    /// A <c>PartInfo</c> without <c>href</c> stands for the SOAP body and is paired with null.
    /// </summary>
    public static IReadOnlyList<(PartInfo Info, SwaPart? Part)> Match(UserMessage message, SwaMessage swa, int maxParts = DefaultMaxParts)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(swa);

        if (swa.Parts.Count > maxParts)
            throw new As4ProcessingException(As4ErrorCode.MimeInconsistency, $"the message has {swa.Parts.Count} parts; at most {maxParts} are accepted.");

        var pairs = new List<(PartInfo, SwaPart?)>();
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var info in message.PayloadInfo)
        {
            if (info.Href is null)
            {
                pairs.Add((info, null));
                continue;
            }
            if (!info.Href.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
                throw new As4ProcessingException(As4ErrorCode.ExternalPayloadError, $"payload reference '{info.Href}' points outside the message.");

            var id = Uri.UnescapeDataString(info.Href["cid:".Length..]);
            if (!referenced.Add(id))
                throw new As4ProcessingException(As4ErrorCode.MimeInconsistency, $"part '{id}' is referenced more than once.");
            if (!swa.Parts.TryGetValue(id, out var part))
                throw new As4ProcessingException(As4ErrorCode.MimeInconsistency, $"the header references part '{id}', which the message does not carry.");
            pairs.Add((info, part));
        }

        var unreferenced = swa.Parts.Keys.FirstOrDefault(id => !referenced.Contains(id));
        if (unreferenced is not null)
            throw new As4ProcessingException(As4ErrorCode.MimeInconsistency, $"part '{unreferenced}' is not referenced by the header.");

        return pairs;
    }
}
