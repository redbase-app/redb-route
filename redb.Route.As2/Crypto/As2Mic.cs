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

    /// <summary>Whether this MIC equals <paramref name="other"/> (digest + algorithm, case-insensitive algorithm).</summary>
    public bool Matches(As2Mic other) =>
        string.Equals(Digest, other.Digest, StringComparison.Ordinal) &&
        string.Equals(Algorithm, other.Algorithm, StringComparison.OrdinalIgnoreCase);
}
