using System.Xml.Serialization;

namespace SerialNumbers.Core.Integration.Xml;

/// <summary>
/// The wire format of a serial number request, exactly as the partner sends it.
/// Validated against <c>SerialNumberRequest.xsd</c> before it is deserialized.
/// </summary>
[XmlRoot("SerialNumberRequest")]
public sealed class SerialNumberRequestXml
{
    [XmlElement("RequestId")]
    public string RequestId { get; set; } = "";

    [XmlElement("Gtin")]
    public string Gtin { get; set; } = "";

    [XmlElement("Quantity")]
    public int Quantity { get; set; }
}
