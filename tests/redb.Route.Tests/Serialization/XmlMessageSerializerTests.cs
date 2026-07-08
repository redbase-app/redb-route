using System.Xml;
using redb.Route.Serialization;

namespace redb.Route.Tests.Serialization;

/// <summary>
/// Tests for <see cref="XmlMessageSerializer"/>.
/// </summary>
public class XmlMessageSerializerTests
{
    private readonly XmlMessageSerializer _sut = new();

    [Fact]
    public void ContentType_IsApplicationXml()
    {
        _sut.ContentType.Should().Be("application/xml");
    }

    [Fact]
    public void Serialize_ReturnsNonEmptyBytes()
    {
        var bytes = _sut.Serialize(new XmlTestOrder { Id = "ORD-1", Amount = 99.5m });

        bytes.Should().NotBeEmpty();
    }

    [Fact]
    public void Serialize_ProducesValidXml()
    {
        var bytes = _sut.Serialize(new XmlTestOrder { Id = "ORD-1", Amount = 42m });
        var xml = System.Text.Encoding.UTF8.GetString(bytes);

        xml.Should().Contain("<XmlTestOrder");
        xml.Should().Contain("<Id>ORD-1</Id>");
        xml.Should().Contain("<Amount>42</Amount>");
    }

    [Fact]
    public void Serialize_OmitsXmlDeclarationByDefault()
    {
        var bytes = _sut.Serialize(new XmlTestOrder { Id = "X" });
        var xml = System.Text.Encoding.UTF8.GetString(bytes);

        xml.Should().NotContain("<?xml");
    }

    [Fact]
    public void Roundtrip_PreservesObject()
    {
        var original = new XmlTestOrder { Id = "ORD-2", Amount = 100.25m };

        var bytes = _sut.Serialize(original);
        var restored = _sut.Deserialize<XmlTestOrder>(bytes);

        restored.Should().NotBeNull();
        restored!.Id.Should().Be("ORD-2");
        restored.Amount.Should().Be(100.25m);
    }

    [Fact]
    public void Deserialize_UntypedOverload_ReturnsCorrectType()
    {
        var original = new XmlTestOrder { Id = "ORD-3", Amount = 50m };

        var bytes = _sut.Serialize(original);
        var restored = _sut.Deserialize(bytes, typeof(XmlTestOrder));

        restored.Should().BeOfType<XmlTestOrder>();
        ((XmlTestOrder)restored!).Id.Should().Be("ORD-3");
    }

    [Fact]
    public void Deserialize_NullData_Throws()
    {
        var act = () => _sut.Deserialize<XmlTestOrder>(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Deserialize_Untyped_NullData_Throws()
    {
        var act = () => _sut.Deserialize(null!, typeof(XmlTestOrder));

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Deserialize_Untyped_NullType_Throws()
    {
        var act = () => _sut.Deserialize(new byte[] { 1 }, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CustomSettings_WithXmlDeclaration_IncludesDeclaration()
    {
        var writerSettings = new XmlWriterSettings
        {
            OmitXmlDeclaration = false,
            Indent = true
        };
        var sut = new XmlMessageSerializer(writerSettings, XmlMessageSerializer.DefaultReaderSettings);

        var bytes = sut.Serialize(new XmlTestOrder { Id = "X" });
        var xml = System.Text.Encoding.UTF8.GetString(bytes);

        xml.Should().Contain("<?xml");
    }

    [Fact]
    public void Constructor_NullWriterSettings_Throws()
    {
        var act = () => new XmlMessageSerializer(null!, XmlMessageSerializer.DefaultReaderSettings);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullReaderSettings_Throws()
    {
        var act = () => new XmlMessageSerializer(XmlMessageSerializer.DefaultWriterSettings, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>Test DTO for XML serialization. Must be public with parameterless ctor for XmlSerializer.</summary>
    public class XmlTestOrder
    {
        public string? Id { get; set; }
        public decimal Amount { get; set; }
    }
}
