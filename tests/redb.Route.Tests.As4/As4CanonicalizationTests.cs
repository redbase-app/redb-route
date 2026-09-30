using System.Security.Cryptography;
using System.Text;
using redb.Route.As4.Mime;
using redb.Route.As4.Security;

namespace redb.Route.Tests.As4;

/// <summary>
/// Ф7: the digest input of the Attachment-Content-Signature-Transform (SwA 1.1 §5.4.2, the rules of WSS4J):
/// Exclusive C14N for XML media types, CRLF line endings for other text, the bytes as they are for the rest.
/// Interop with Holodeck over these rules is <c>As4HolodeckLiveTests.UncompressedTextPayload_*</c>.
/// </summary>
public class As4CanonicalizationTests
{
    private static byte[] Digest(string contentType, string content) =>
        AttachmentCanonicalizer.Sha256(new SwaPart("p", contentType, Encoding.UTF8.GetBytes(content)));

    private static byte[] Sha(string canonical) => SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

    [Theory]
    [InlineData("text/xml")]
    [InlineData("text/xml; charset=UTF-8")]
    [InlineData("application/xml")]
    [InlineData("application/soap+xml")]
    [InlineData("image/svg+xml")]
    public void XmlMediaTypes_AreExclusiveC14N(string contentType)
    {
        Digest(contentType, "<?xml version='1.0'?><a  x='1'><b/><!-- note --></a>")
            .Should().Equal(Sha("<a x=\"1\"><b></b></a>"));
    }

    [Theory]
    [InlineData("a\nb", "a\r\nb")]
    [InlineData("a\rb", "a\r\nb")]
    [InlineData("a\r\nb", "a\r\nb")]
    [InlineData("a\n\nb\r\r", "a\r\n\r\nb\r\n\r\n")]
    public void OtherText_HasCrlfLineEndings(string content, string canonical)
    {
        Digest("text/plain", content).Should().Equal(Sha(canonical));
    }

    [Fact]
    public void CrlfSplitAcrossReads_IsOneLineEnding()
    {
        // The reader works in 80 KB blocks: a CR that ends one block and the LF that starts the next are one CRLF.
        var head = new string('x', 81919);
        Digest("text/csv", head + "\r\n" + "tail").Should().Equal(Sha(head + "\r\n" + "tail"));
    }

    [Theory]
    [InlineData("application/octet-stream")]
    [InlineData("application/gzip")]
    [InlineData("application/pdf")]
    public void OtherMediaTypes_AreTheBytesAsTheyAre(string contentType)
    {
        Digest(contentType, "<a  x='1'/>\n").Should().Equal(Sha("<a  x='1'/>\n"));
    }

    [Fact]
    public void XmlAttachmentWithADoctype_IsRefused_AsAFailedSignature()
    {
        var act = () => Digest("text/xml", "<!DOCTYPE a [<!ENTITY e \"x\">]><a>&e;</a>");

        act.Should().Throw<CryptographicException>().WithMessage("*not well-formed XML*");
    }
}
