using System.Text;
using FluentAssertions;
using redb.Route.Soap;

namespace redb.Route.Tests.Soap;

/// <summary>Unit tests for SOAP envelope build/parse (1.1 & 1.2), fault detection, and Content-Type.</summary>
public class SoapEnvelopeTests
{
    private const string Payload = "<GetFares xmlns=\"urn:test\"><from>SVO</from></GetFares>";

    [Theory]
    [InlineData(SoapVersion.Soap11)]
    [InlineData(SoapVersion.Soap12)]
    public void Build_Then_Parse_RoundTripsBody(SoapVersion version)
    {
        var envelope = SoapEnvelope.Build(Payload, version);
        var parsed = SoapEnvelope.Parse(envelope, version);

        parsed.IsFault.Should().BeFalse();
        parsed.BodyXml.Should().Contain("GetFares").And.Contain("SVO");
    }

    [Fact]
    public void Build_Soap11_UsesTextXml()
    {
        SoapEnvelope.ContentType(SoapVersion.Soap11, "GetFares").Should().StartWith("text/xml");
    }

    [Fact]
    public void Build_Soap12_UsesSoapXml_WithActionParam()
    {
        var ct = SoapEnvelope.ContentType(SoapVersion.Soap12, "GetFares");
        ct.Should().StartWith("application/soap+xml").And.Contain("action=\"GetFares\"");
    }

    [Fact]
    public void Parse_Soap11_Fault_IsDetected()
    {
        var fault = SoapEnvelope.BuildFault("boom", SoapVersion.Soap11, "soap:Client");
        var parsed = SoapEnvelope.Parse(fault, SoapVersion.Soap11);

        parsed.IsFault.Should().BeTrue();
        parsed.FaultCode.Should().Be("soap:Client");
        parsed.FaultString.Should().Be("boom");
    }

    [Fact]
    public void Parse_Soap12_Fault_IsDetected()
    {
        var fault = SoapEnvelope.BuildFault("kaboom", SoapVersion.Soap12, "soap:Sender");
        var parsed = SoapEnvelope.Parse(fault, SoapVersion.Soap12);

        parsed.IsFault.Should().BeTrue();
        parsed.FaultCode.Should().Be("soap:Sender");
        parsed.FaultString.Should().Be("kaboom");
    }

    [Fact]
    public void Build_WithHeader_PutsHeaderInEnvelopeHeaderPlane()
    {
        var envelope = SoapEnvelope.Build(Payload, SoapVersion.Soap11, "<wsa:To xmlns:wsa=\"urn:ws-a\">svc</wsa:To>");
        var headers = SoapEnvelope.ReadHeaders(envelope, SoapVersion.Soap11).ToList();

        headers.Should().ContainSingle();
        headers[0].Value.Should().Be("svc");
    }

    /// <summary>
    /// A fault code is a QName in both SOAP versions, so a prefixed code means nothing unless the prefix
    /// is bound on the envelope. It was written unbound, which reads fine to the eye and does not resolve
    /// in a client that treats it as the QName the specification says it is — and the clients that branch
    /// on fault codes are exactly those.
    /// </summary>
    [Theory]
    [InlineData(SoapVersion.Soap11)]
    [InlineData(SoapVersion.Soap12)]
    public void A_prefixed_fault_code_resolves_as_a_qname(SoapVersion version)
    {
        var xml = System.Text.Encoding.UTF8.GetString(
            SoapEnvelope.BuildFault("no", version, "wst:FailedAuthentication"));

        var envelope = System.Xml.Linq.XElement.Parse(xml);
        var code = envelope.Descendants()
            .First(e => e.Name.LocalName is "faultcode" or "Value").Value;

        code.Should().Be("wst:FailedAuthentication");

        // The point of the test: resolving the prefix has to give the WS-Trust namespace, not nothing.
        var prefix = code.Split(':')[0];
        envelope.GetNamespaceOfPrefix(prefix).Should().NotBeNull();
        envelope.GetNamespaceOfPrefix(prefix)!.NamespaceName
            .Should().Be("http://docs.oasis-open.org/ws-sx/ws-trust/200512");
    }

    /// <summary>
    /// A code in a namespace of the caller's own is bound from what they gave, because the connector has
    /// no way to guess it and guessing wrong would be worse than leaving it unresolved.
    /// </summary>
    [Fact]
    public void A_caller_supplied_namespace_binds_its_own_prefix()
    {
        var xml = System.Text.Encoding.UTF8.GetString(
            SoapEnvelope.BuildFault("no", SoapVersion.Soap11, "acme:QuotaExceeded", "urn:acme:faults"));

        System.Xml.Linq.XElement.Parse(xml).GetNamespaceOfPrefix("acme")!.NamespaceName
            .Should().Be("urn:acme:faults");
    }

    /// <summary>
    /// The default code keeps the envelope exactly as it was: `soap` is already bound, and nothing else
    /// is added for it.
    /// </summary>
    [Fact]
    public void The_default_fault_code_adds_no_declaration()
    {
        var xml = System.Text.Encoding.UTF8.GetString(
            SoapEnvelope.BuildFault("no", SoapVersion.Soap11));

        xml.Should().Contain("soap:Server");
        System.Text.RegularExpressions.Regex.Matches(xml, "xmlns:").Should().HaveCount(1);
    }
}
