using System.Text;

namespace redb.Route.Soap;

/// <summary>
/// MTOM/XOP wire packaging: a SOAP envelope plus binary attachments as a <c>multipart/related</c> body
/// (RFC 2387 + the XOP spec). The root part is the envelope (declared <c>application/xop+xml</c>), each
/// attachment is a MIME part addressed by Content-ID and referenced from the body via
/// <c>&lt;xop:Include href="cid:..."/&gt;</c>. Hand-rolled (no MimeKit dependency) and scoped to the shapes
/// real MTOM stacks emit. The delimiter search is line-anchored (RFC 2046: a boundary is <c>CRLF--boundary</c>
/// at a line start) so boundary-looking bytes inside a binary part cannot cause a false split; the consumer
/// parses regardless of transfer-encoding; the producer writes <c>binary</c>.
/// </summary>
internal static class SoapMultipart
{
    public const string XopNs = "http://www.w3.org/2004/08/xop/include";
    private const string RootCid = "root.message@redb.route";
    private static readonly byte[] Crlf = { (byte)'\r', (byte)'\n' };

    /// <summary>
    /// Wraps the envelope + attachments into a multipart/related body and returns the HTTP Content-Type.
    /// <paramref name="action"/>, when supplied, is added as an <c>action</c> parameter on the multipart
    /// Content-Type — the SOAP 1.2 way to carry the action once the envelope's own media type is hidden.
    /// </summary>
    public static (byte[] body, string contentType) Write(
        byte[] rootXml, string rootMediaType, IReadOnlyList<SoapAttachment> attachments, string? action = null)
    {
        var boundary = "----=_redb_mtom_" + Guid.NewGuid().ToString("N");
        var startInfo = MediaTypeBase(rootMediaType); // text/xml (1.1) or application/soap+xml (1.2)

        using var ms = new MemoryStream();
        void Line(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); ms.Write(Crlf, 0, 2); }

        // Root part: the SOAP envelope, declared as XOP.
        Line("--" + boundary);
        Line($"Content-Type: application/xop+xml; charset=UTF-8; type=\"{startInfo}\"");
        Line("Content-Transfer-Encoding: binary");
        Line($"Content-ID: <{RootCid}>");
        Line(string.Empty);
        ms.Write(rootXml, 0, rootXml.Length);
        ms.Write(Crlf, 0, 2);

        // Attachment parts. Content-ID is bracketed exactly once (a caller-supplied id may already carry them).
        foreach (var att in attachments)
        {
            Line("--" + boundary);
            Line($"Content-Type: {att.ContentType}");
            Line("Content-Transfer-Encoding: binary");
            Line($"Content-ID: <{StripBrackets(att.ContentId)}>");
            Line(string.Empty);
            ms.Write(att.Content, 0, att.Content.Length);
            ms.Write(Crlf, 0, 2);
        }

        Line("--" + boundary + "--");

        var contentType =
            $"multipart/related; boundary=\"{boundary}\"; type=\"application/xop+xml\"; " +
            $"start=\"<{RootCid}>\"; start-info=\"{startInfo}\"";
        if (!string.IsNullOrEmpty(action))
            contentType += $"; action=\"{action}\"";
        return (ms.ToArray(), contentType);
    }

    /// <summary>True when the HTTP Content-Type is a multipart/related (MTOM) envelope.</summary>
    public static bool IsMultipartRelated(string? contentType) =>
        contentType is not null && contentType.Contains("multipart/related", StringComparison.OrdinalIgnoreCase);

    /// <summary>The <c>start-info</c> parameter (the inner SOAP media type) of a multipart/related Content-Type.</summary>
    public static string? StartInfo(string contentType) => Param(contentType, "start-info");

    /// <summary>
    /// Parses a multipart/related body: returns the root part bytes (the SOAP envelope, per the <c>start</c>
    /// parameter, an <c>application/xop+xml</c> Content-Type, or the first part) and the attachments keyed by
    /// Content-ID. Throws <see cref="FormatException"/> when the boundary is missing or matches no part, rather
    /// than silently yielding an empty envelope.
    /// </summary>
    public static (byte[] root, IReadOnlyList<SoapAttachment> attachments) Parse(byte[] body, string contentType)
    {
        var boundary = Param(contentType, "boundary")
            ?? throw new FormatException("multipart/related Content-Type has no boundary.");
        var startCid = StripBrackets(Param(contentType, "start"));

        var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
        var rawParts = SplitParts(body, delimiter);
        if (rawParts.Count == 0)
            throw new FormatException($"multipart/related body has no parts for boundary '{boundary}'.");

        // Classify every part once, then pick the root by index — never by mutating the attachment list.
        var parsed = new (string cid, string type, byte[] content)[rawParts.Count];
        var rootIndex = -1;
        for (var i = 0; i < rawParts.Count; i++)
        {
            var (headers, content) = SplitHeadersAndBody(rawParts[i]);
            var cid = StripBrackets(HeaderValue(headers, "Content-ID")) ?? string.Empty;
            var type = HeaderValue(headers, "Content-Type") ?? "application/octet-stream";
            parsed[i] = (cid, type, content);

            var isRoot = type.Contains("application/xop+xml", StringComparison.OrdinalIgnoreCase)
                         || (startCid is not null && cid == startCid);
            if (isRoot && rootIndex < 0) rootIndex = i;
        }
        if (rootIndex < 0) rootIndex = 0; // no explicit root marker: the first part is the SOAP envelope

        var attachments = new List<SoapAttachment>();
        for (var i = 0; i < parsed.Length; i++)
        {
            if (i == rootIndex) continue;
            var (cid, type, content) = parsed[i];
            if (!string.IsNullOrEmpty(cid))
                attachments.Add(new SoapAttachment(cid, type, content)); // keep the full Content-Type (params included)
        }

        return (parsed[rootIndex].content, attachments);
    }

    // ── byte-level MIME helpers ──

    private static List<byte[]> SplitParts(byte[] body, byte[] delimiter)
    {
        var parts = new List<byte[]>();
        int idx = FindDelimiter(body, delimiter, 0);
        while (idx >= 0)
        {
            int partStart = idx + delimiter.Length;
            // A closing delimiter is followed by "--"; stop there.
            if (partStart + 1 < body.Length && body[partStart] == (byte)'-' && body[partStart + 1] == (byte)'-')
                break;
            // Skip the CRLF (or lone LF) after the delimiter.
            if (partStart < body.Length && body[partStart] == (byte)'\r') partStart++;
            if (partStart < body.Length && body[partStart] == (byte)'\n') partStart++;

            int next = FindDelimiter(body, delimiter, partStart);
            if (next < 0) break;

            // The content ends at the CRLF immediately before the next delimiter.
            int end = next;
            if (end >= 2 && body[end - 1] == (byte)'\n' && body[end - 2] == (byte)'\r') end -= 2;
            else if (end >= 1 && body[end - 1] == (byte)'\n') end -= 1;

            parts.Add(body[partStart..Math.Max(partStart, end)]);
            idx = next;
        }
        return parts;
    }

    /// <summary>
    /// Finds the next occurrence of <c>--boundary</c> that is line-anchored (at the start of the buffer or
    /// immediately after a LF), so identical bytes inside a binary part are not mistaken for a delimiter.
    /// </summary>
    private static int FindDelimiter(byte[] body, byte[] delimiter, int start)
    {
        int i = start;
        while ((i = IndexOf(body, delimiter, i)) >= 0)
        {
            if (i == 0 || body[i - 1] == (byte)'\n') return i;
            i += 1; // not at a line start — a coincidental match inside content; keep searching
        }
        return -1;
    }

    private static (string headers, byte[] body) SplitHeadersAndBody(byte[] part)
    {
        var sep = new byte[] { (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n' };
        int i = IndexOf(part, sep, 0);
        if (i < 0)
        {
            // Tolerate bare-LF header separators.
            var sepLf = new byte[] { (byte)'\n', (byte)'\n' };
            i = IndexOf(part, sepLf, 0);
            if (i < 0) return (HeaderText(part, 0, part.Length), Array.Empty<byte>());
            return (HeaderText(part, 0, i), part[(i + 2)..]);
        }
        return (HeaderText(part, 0, i), part[(i + 4)..]);
    }

    // Latin1 maps every byte 1:1 to a char, so header bytes survive without the '?' substitution ASCII would apply.
    private static string HeaderText(byte[] buffer, int index, int count) => Encoding.Latin1.GetString(buffer, index, count);

    private static string? HeaderValue(string headers, string name)
    {
        foreach (var line in headers.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            int c = l.IndexOf(':');
            if (c > 0 && l.AsSpan(0, c).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return l[(c + 1)..].Trim();
        }
        return null;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (int i = start; i <= haystack.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    // ── Content-Type parameter helpers ──

    private static string MediaTypeBase(string contentType)
    {
        int semi = contentType.IndexOf(';');
        return (semi < 0 ? contentType : contentType[..semi]).Trim();
    }

    private static string? Param(string contentType, string name)
    {
        // Split on top-level ';' only — a quoted value may legally contain ';' (e.g. boundary="a;b" or
        // start-info="application/soap+xml; action=..."), so splitting inside quotes would corrupt it.
        int i = 0, n = contentType.Length;
        while (i < n)
        {
            int start = i;
            var inQuotes = false;
            while (i < n && (inQuotes || contentType[i] != ';'))
            {
                if (contentType[i] == '"') inQuotes = !inQuotes;
                i++;
            }
            var seg = contentType[start..i].Trim();
            i++; // past the ';'

            int eq = seg.IndexOf('=');
            if (eq > 0 && seg.AsSpan(0, eq).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                var value = seg[(eq + 1)..].Trim();
                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1]; // one quote pair
                return value;
            }
        }
        return null;
    }

    private static string? StripBrackets(string? cid) => cid?.Trim().Trim('<', '>');
}
