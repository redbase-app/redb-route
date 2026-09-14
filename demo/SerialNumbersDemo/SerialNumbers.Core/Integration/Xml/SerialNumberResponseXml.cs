using System.Xml.Serialization;

namespace SerialNumbers.Core.Integration.Xml;

/// <summary>The wire format of the response sent back to the partner.</summary>
[XmlRoot("SerialNumberResponse")]
public sealed class SerialNumberResponseXml
{
    [XmlElement("RequestId")]
    public string RequestId { get; set; } = "";

    [XmlElement("Gtin")]
    public string Gtin { get; set; } = "";

    /// <summary><c>Accepted</c> or <c>Rejected</c>.</summary>
    [XmlElement("Status")]
    public string Status { get; set; } = "";

    [XmlElement("Reason")]
    public string? Reason { get; set; }

    [XmlElement("Quantity")]
    public int Quantity { get; set; }

    [XmlElement("FirstSerial")]
    public string? FirstSerial { get; set; }

    [XmlElement("LastSerial")]
    public string? LastSerial { get; set; }
}
