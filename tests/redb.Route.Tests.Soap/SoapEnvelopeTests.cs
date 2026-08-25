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
}
