using System.IO.Compression;
using System.Text;
using System.Xml;
using redb.Route.As4;
using redb.Route.As4.Security;
using redb.Route.Core;

namespace redb.Route.Tests.As4;

/// <summary>
/// Hand-built AS4 user messages for the security tests of Ф1 — before the ebMS model of Ф2 exists. One
/// attachment, compressed, with the eDelivery four-corner properties.
/// </summary>
internal static class As4TestMessages
{
    /// <summary>A SOAP 1.2 envelope with one <c>eb:UserMessage</c> whose payload is the part <paramref name="contentId"/>.</summary>
    public static XmlDocument UserMessage(string messageId, string contentId,
        string from = "sender", string to = "receiver",
        string? agreementRef = null, string service = "urn:example", string? serviceType = null, string action = "Submit",
        bool fourCorner = true, string? uncompressedMimeType = null, params string[] moreContentIds)
    {
        var properties = fourCorner
            ? "<eb:MessageProperties><eb:Property name=\"originalSender\">urn:oasis:names:tc:ebcore:partyid-type:unregistered:C1</eb:Property><eb:Property name=\"finalRecipient\">urn:oasis:names:tc:ebcore:partyid-type:unregistered:C4</eb:Property></eb:MessageProperties>"
            : "";
        var partInfos = string.Concat(new[] { contentId }.Concat(moreContentIds).Select(id =>
            uncompressedMimeType is null
                ? $"<eb:PartInfo href=\"cid:{id}\"><eb:PartProperties><eb:Property name=\"MimeType\">text/xml</eb:Property><eb:Property name=\"CompressionType\">application/gzip</eb:Property></eb:PartProperties></eb:PartInfo>"
                : $"<eb:PartInfo href=\"cid:{id}\"><eb:PartProperties><eb:Property name=\"MimeType\">{uncompressedMimeType}</eb:Property></eb:PartProperties></eb:PartInfo>"));
        var agreement = agreementRef is null ? "" : $"<eb:AgreementRef>{agreementRef}</eb:AgreementRef>";
        var serviceTypeAttribute = serviceType is null ? "" : $" type=\"{serviceType}\"";
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

        return SafeXml.LoadDocument(Encoding.UTF8.GetBytes($"""
            <soapenv:Envelope xmlns:soapenv="http://www.w3.org/2003/05/soap-envelope" xmlns:eb="{WsSecurityNames.Eb}">
              <soapenv:Header>
                <eb:Messaging soapenv:mustUnderstand="true">
                  <eb:UserMessage>
                    <eb:MessageInfo><eb:Timestamp>{timestamp}</eb:Timestamp><eb:MessageId>{messageId}</eb:MessageId></eb:MessageInfo>
                    <eb:PartyInfo>
                      <eb:From><eb:PartyId>{from}</eb:PartyId><eb:Role>{As4Partner.InitiatorRole}</eb:Role></eb:From>
                      <eb:To><eb:PartyId>{to}</eb:PartyId><eb:Role>{As4Partner.ResponderRole}</eb:Role></eb:To>
                    </eb:PartyInfo>
                    <eb:CollaborationInfo>{agreement}<eb:Service{serviceTypeAttribute}>{service}</eb:Service><eb:Action>{action}</eb:Action><eb:ConversationId>conversation-1</eb:ConversationId></eb:CollaborationInfo>
                    {properties}
                    <eb:PayloadInfo>{partInfos}</eb:PayloadInfo>
                  </eb:UserMessage>
                </eb:Messaging>
              </soapenv:Header>
              <soapenv:Body/>
            </soapenv:Envelope>
            """));
    }

    /// <summary>A self-signed RSA key pair with a Subject Key Identifier, usable for decryption on every platform.</summary>
    public static System.Security.Cryptography.X509Certificates.X509Certificate2 KeyPair(string subject) =>
        KeyPair(subject, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

    /// <summary>A key pair valid from <paramref name="notBefore"/> to <paramref name="notAfter"/> (an expired or future one, for validity tests).</summary>
    public static System.Security.Cryptography.X509Certificates.X509Certificate2 KeyPair(string subject, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(subject, rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        using var created = request.CreateSelfSigned(notBefore, notAfter);
#pragma warning disable SYSLIB0057 // net8.0 is a target framework; X509CertificateLoader is net9+.
        return new System.Security.Cryptography.X509Certificates.X509Certificate2(
            created.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx), (string?)null,
            System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.Exportable);
    }

    /// <summary>The certificate of <paramref name="certificate"/> without its private key.</summary>
    public static System.Security.Cryptography.X509Certificates.X509Certificate2 PublicOnly(System.Security.Cryptography.X509Certificates.X509Certificate2 certificate) =>
        new(certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert));
#pragma warning restore SYSLIB0057

    public static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
            gzip.Write(data);
        return output.ToArray();
    }

    /// <summary>Flips one bit of a part's content, as a tampering attacker would.</summary>
    public static void Flip(redb.Route.As4.Mime.SwaPart part, int index)
    {
        var bytes = part.ToArray();
        bytes[index] ^= 0x01;
        part.Replace(new MemoryStream(bytes, writable: false));
    }

    public static byte[] Gunzip(byte[] data)
    {
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}
