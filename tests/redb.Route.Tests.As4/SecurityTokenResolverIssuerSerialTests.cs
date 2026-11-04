using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml;
using redb.Route.As4.Security;

namespace redb.Route.Tests.As4;

/// <summary>
/// A <c>ds:X509IssuerSerial</c> reference finds our certificate whatever spacing the sender puts between the parts
/// of the issuer name: WSS4J and Domibus write RFC 2253 ("CN=a,OU=b,C=BE"), .NET writes "CN=a, OU=b, C=BE".
/// </summary>
public class SecurityTokenResolverIssuerSerialTests
{
    private const string Subject = "CN=Conformance Testing CA, OU=eDelivery, O=Building Blocks Office, C=BE";
    private static readonly X509Certificate2 Ours = SelfSigned(Subject);

    [Theory]
    [InlineData("CN=Conformance Testing CA,OU=eDelivery,O=Building Blocks Office,C=BE")]
    [InlineData("CN=Conformance Testing CA, OU=eDelivery, O=Building Blocks Office, C=BE")]
    [InlineData("cn=Conformance Testing CA,ou=eDelivery,o=Building Blocks Office,c=BE")]
    public void IssuerSerial_FindsOurCertificate_WhateverTheSpacingOfTheIssuerName(string issuerName)
    {
        var (keyInfo, envelope) = KeyInfo(issuerName, Serial(Ours));

        var resolved = SecurityTokenResolver.Resolve(keyInfo, envelope, [Ours]);

        resolved.Should().NotBeNull();
        resolved!.Thumbprint.Should().Be(Ours.Thumbprint);
    }

    [Fact]
    public void IssuerSerial_OfAnotherSerial_FindsNothing()
    {
        var (keyInfo, envelope) = KeyInfo("CN=Conformance Testing CA,OU=eDelivery,O=Building Blocks Office,C=BE",
            Serial(Ours) + 1);

        SecurityTokenResolver.Resolve(keyInfo, envelope, [Ours]).Should().BeNull();
    }

    [Fact]
    public void IssuerSerial_OfAnotherIssuer_FindsNothing()
    {
        var (keyInfo, envelope) = KeyInfo("CN=Conformance Testing CA,OU=eDelivery,O=Building Blocks Office,C=DE",
            Serial(Ours));

        SecurityTokenResolver.Resolve(keyInfo, envelope, [Ours]).Should().BeNull();
    }

    private static (XmlElement KeyInfo, XmlDocument Envelope) KeyInfo(string issuerName, BigInteger serial)
    {
        var doc = new XmlDocument();
        doc.LoadXml(
            $"""
            <ds:KeyInfo xmlns:ds="{WsSecurityNames.Ds}" xmlns:wsse="{WsSecurityNames.Wsse}">
              <wsse:SecurityTokenReference>
                <ds:X509Data>
                  <ds:X509IssuerSerial>
                    <ds:X509IssuerName>{issuerName}</ds:X509IssuerName>
                    <ds:X509SerialNumber>{serial}</ds:X509SerialNumber>
                  </ds:X509IssuerSerial>
                </ds:X509Data>
              </wsse:SecurityTokenReference>
            </ds:KeyInfo>
            """);
        return (doc.DocumentElement!, doc);
    }

    private static BigInteger Serial(X509Certificate2 certificate) =>
        new(certificate.GetSerialNumber(), isUnsigned: true, isBigEndian: false);

    private static X509Certificate2 SelfSigned(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
#pragma warning disable SYSLIB0057 // net8.0 is a target framework; X509CertificateLoader is net9+.
        return new X509Certificate2(created.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
#pragma warning restore SYSLIB0057
    }
}
