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

    /// <summary>Part property naming the character set of an XML payload before compression.</summary>
    public const string CharacterSetProperty = "CharacterSet";

    /// <summary>
    /// Whether <paramref name="value"/> is an encoding name of XML 1.0 §4.3.3 (<c>EncName</c>: a Latin letter, then
    /// Latin letters, digits, <c>.</c>, <c>_</c> and <c>-</c>), which the profile requires of the
    /// <c>CharacterSet</c> part property (eDelivery AS4 1.16 §3.2.3).
    /// </summary>
    public static bool IsCharacterSetName(string? value)
    {
        if (string.IsNullOrEmpty(value) || !char.IsAsciiLetter(value[0]))
            return false;
        foreach (var c in value)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
                return false;
        return true;
    }

    /// <summary>
    /// Throws <see cref="As4ErrorCode.ValueInconsistent"/> when a part of <paramref name="message"/> names a
    /// <c>CharacterSet</c> that is not an encoding name.
    /// </summary>
    public static void CheckCharacterSets(UserMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        foreach (var info in message.PayloadInfo)
            foreach (var property in info.Properties)
                if (property.Name == CharacterSetProperty && !IsCharacterSetName(property.Value))
                    throw new As4ProcessingException(As4ErrorCode.ValueInconsistent,
                        $"part '{info.Href}' names CharacterSet '{property.Value}', which is not an encoding name of XML 1.0 §4.3.3.");
    }

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
