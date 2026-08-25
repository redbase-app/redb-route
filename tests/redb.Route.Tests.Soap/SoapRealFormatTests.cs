using FluentAssertions;
using redb.Route.Soap;

namespace redb.Route.Tests.Soap;

/// <summary>
/// Ф8-lite (offline, no vulnerable dependency): robustness against the wire formats real SOAP stacks
/// (WCF/CXF) actually emit — arbitrary namespace prefixes, WS-Addressing headers with mustUnderstand,
/// SOAP 1.2 faults. Our parser is namespace-driven, so prefixes must not matter. Live interop against an
/// independent stack (Java CXF in Docker) is the remaining net-gated step; see the plan.
/// </summary>
public class SoapRealFormatTests
{
    // A .NET WCF response: envelope prefix "s", WS-Addressing header with s:mustUnderstand, default-ns body.
    private const string WcfStyle =
        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
          "<s:Header>" +
            "<a:Action s:mustUnderstand=\"1\" xmlns:a=\"http://www.w3.org/2005/08/addressing\">urn:GetFares</a:Action>" +
          "</s:Header>" +
          "<s:Body><GetFaresResponse xmlns=\"urn:test\"><fare>100</fare></GetFaresResponse></s:Body>" +
        "</s:Envelope>";

    // An Apache CXF response: prefix "soapenv", body element with its own prefix "ns1".
    private const string CxfStyle =
        "<soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
          "<soapenv:Body><ns1:getFaresResponse xmlns:ns1=\"urn:test\"><ns1:fare>200</ns1:fare></ns1:getFaresResponse></soapenv:Body>" +
        "</soapenv:Envelope>";

    [Fact]
    public void Parses_WcfStyle_PrefixedEnvelope()
    {
        var parsed = SoapEnvelope.Parse(System.Text.Encoding.UTF8.GetBytes(WcfStyle), SoapVersion.Soap11);
        parsed.IsFault.Should().BeFalse();
        parsed.BodyXml.Should().Contain("GetFaresResponse").And.Contain("100");
    }

    [Fact]
    public void Reads_WcfStyle_EnvelopeHeader_WithMustUnderstand()
    {
        var headers = SoapEnvelope.ReadHeaders(System.Text.Encoding.UTF8.GetBytes(WcfStyle), SoapVersion.Soap11).ToList();
        headers.Should().ContainSingle();
        headers[0].Name.LocalName.Should().Be("Action");
        headers[0].Value.Should().Be("urn:GetFares");
    }

    [Fact]
    public void Parses_CxfStyle_PrefixedBodyElement()
    {
        var parsed = SoapEnvelope.Parse(System.Text.Encoding.UTF8.GetBytes(CxfStyle), SoapVersion.Soap11);
        parsed.IsFault.Should().BeFalse();
        parsed.BodyXml.Should().Contain("getFaresResponse").And.Contain("200");
    }

    [Fact]
    public void Parses_Soap12_Fault_FromRealShape()
    {
        const string fault12 =
            "<env:Envelope xmlns:env=\"http://www.w3.org/2003/05/soap-envelope\">" +
              "<env:Body><env:Fault>" +
                "<env:Code><env:Value>env:Sender</env:Value></env:Code>" +
                "<env:Reason><env:Text xml:lang=\"en\">Invalid fare request</env:Text></env:Reason>" +
              "</env:Fault></env:Body>" +
            "</env:Envelope>";
        var parsed = SoapEnvelope.Parse(System.Text.Encoding.UTF8.GetBytes(fault12), SoapVersion.Soap12);
        parsed.IsFault.Should().BeTrue();
        parsed.FaultCode.Should().Be("env:Sender");
        parsed.FaultString.Should().Be("Invalid fare request");
    }
}
