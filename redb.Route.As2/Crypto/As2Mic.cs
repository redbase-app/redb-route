namespace redb.Route.As2.Crypto;

/// <summary>
/// An AS2 Message Integrity Check: a base64 message digest plus the algorithm name, in the wire format
/// AS2 uses for the <c>Received-Content-MIC</c> MDN field — <c>"&lt;base64-digest&gt;, &lt;algorithm&gt;"</c>
/// (RFC 4130 §7.3.1). The sender computes it over the content it signed; the receiver recomputes it and
/// returns it in the MDN so the sender can confirm the partner got exactly what was sent, intact.
/// </summary>
public readonly record struct As2Mic(string Digest, string Algorithm)
{
    /// <summary>Renders the MIC in AS2 wire format: <c>"base64digest, algorithm"</c>.</summary>
    public override string ToString() => $"{Digest}, {Algorithm}";

    /// <summary>
    /// Parses an AS2 <c>Received-Content-MIC</c> value (<c>"base64digest, algorithm"</c>). The digest is
    /// base64 and never contains a comma, so we split on the last comma to be robust.
    /// </summary>
    public static As2Mic Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var comma = value.LastIndexOf(',');
        if (comma < 0)
            throw new FormatException($"Not a valid AS2 MIC (missing ', algorithm'): '{value}'.");
        return new As2Mic(value[..comma].Trim(), value[(comma + 1)..].Trim());
    }

    /// <summary>
    /// Whether this MIC equals <paramref name="other"/>: the same digest bytes (base64 compared after decoding, so folded
    /// lines, spaces and missing padding do not matter) and the same algorithm (<c>sha-256</c> and <c>SHA256</c> are one).
    /// </summary>
    public bool Matches(As2Mic other)
    {
        if (!string.Equals(AlgorithmKey(Algorithm), AlgorithmKey(other.Algorithm), StringComparison.Ordinal))
            return false;
        var mine = DigestBytes(Digest);
        var theirs = DigestBytes(other.Digest);
        return mine is not null && theirs is not null && mine.AsSpan().SequenceEqual(theirs);
    }

    private static string AlgorithmKey(string algorithm) =>
        algorithm.Replace("-", "", StringComparison.Ordinal).Trim().ToLowerInvariant();

    private static byte[]? DigestBytes(string digest)
    {
        var compact = string.Concat(digest.Where(c => !char.IsWhiteSpace(c)));
        if (compact.Length % 4 != 0)
            compact = compact.PadRight(compact.Length + (4 - compact.Length % 4), '=');
        var buffer = new byte[compact.Length * 3 / 4];
        return Convert.TryFromBase64String(compact, buffer, out var written) && written > 0 ? buffer[..written] : null;
    }
}
