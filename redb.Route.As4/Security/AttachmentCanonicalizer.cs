using System.Security.Cryptography;
using System.Security.Cryptography.Xml;
using System.Text.RegularExpressions;
using System.Xml;
using redb.Route.As4.Mime;
using redb.Route.Core;

namespace redb.Route.As4.Security;

/// <summary>
/// The digest input of the Attachment-Content-Signature-Transform: the part's content in its MIME canonical form
/// (OASIS WSS SwA Profile 1.1 §5.4.2). The rules and the media type patterns are those of Apache WSS4J, which
/// Holodeck B2B and Domibus sign and verify with:
/// <list type="bullet">
/// <item>XML (<c>text/xml</c>, <c>application/xml</c>, <c>application/*+xml</c>, <c>image/*+xml</c>) — Exclusive XML
/// Canonicalization without comments, with no InclusiveNamespaces prefix list;</item>
/// <item>any other <c>text/*</c> — line endings normalized to CRLF;</item>
/// <item>anything else — the content as it is.</item>
/// </list>
/// The media type is the part's own: with AS4 compression it is <c>application/gzip</c>, so the rules bite only on
/// payloads sent uncompressed.
/// </summary>
internal static class AttachmentCanonicalizer
{
    private static readonly Regex Xml = new(@"^(text/xml|application/xml|(application|image)/[^;]*\+xml)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Text = new(@"^text/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>SHA-256 of the canonical form of <paramref name="part"/>.</summary>
    public static byte[] Sha256(SwaPart part)
    {
        ArgumentNullException.ThrowIfNull(part);

        if (Xml.IsMatch(part.ContentType))
            return SHA256.HashData(ExclusiveC14N(part));

        if (Text.IsMatch(part.ContentType))
            return CrlfSha256(part.OpenRead());

        return part.Sha256();
    }

    private static Stream ExclusiveC14N(SwaPart part)
    {
        // Foreign XML: the core SafeXml reader settings (no DTD), whitespace kept, as the transform reads the content.
        var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(part.OpenRead(), SafeXml.Settings());
            document.Load(reader);
        }
        catch (XmlException e)
        {
            // Its digest cannot be computed, so the signature over it cannot hold.
            throw new CryptographicException($"Attachment '{part.ContentId}' is declared {part.ContentType} but is not well-formed XML.", e);
        }

        var transform = new XmlDsigExcC14NTransform(includeComments: false);
        transform.LoadInput(document);
        return (Stream)transform.GetOutput(typeof(Stream));
    }

    /// <summary>
    /// SHA-256 of <paramref name="input"/> with every CR, LF and CRLF written as CRLF — the rule of WSS4J's
    /// <c>CRLFOutputStream</c>.
    /// </summary>
    private static byte[] CrlfSha256(Stream input)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        ReadOnlySpan<byte> crlf = "\r\n"u8;
        var buffer = new byte[81920];
        var afterCr = false;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                var b = buffer[i];
                if (b == (byte)'\r')
                {
                    hash.AppendData(buffer, start, i - start);
                    hash.AppendData(crlf);
                    start = i + 1;
                    afterCr = true;
                }
                else if (b == (byte)'\n')
                {
                    hash.AppendData(buffer, start, i - start);
                    if (!afterCr) hash.AppendData(crlf);
                    start = i + 1;
                    afterCr = false;
                }
                else
                {
                    afterCr = false;
                }
            }
            hash.AppendData(buffer, start, read - start);
        }
        return hash.GetHashAndReset();
    }
}
