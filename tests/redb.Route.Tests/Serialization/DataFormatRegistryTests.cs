using redb.Route.Abstractions;
using redb.Route.Serialization;

namespace redb.Route.Tests.Serialization;

/// <summary>
/// Tests for <see cref="DataFormatRegistry"/>.
/// </summary>
public class DataFormatRegistryTests
{
    [Fact]
    public void ExactMatch_Json_ReturnsSerializer()
    {
        var registry = new DataFormatRegistry();
        var s = registry.GetSerializer("application/json");
        s.Should().NotBeNull();
        s!.ContentType.Should().Be("application/json");
    }

    [Fact]
    public void ExactMatch_Xml_ReturnsSerializer()
    {
        var registry = new DataFormatRegistry();
        registry.GetSerializer("application/xml").Should().NotBeNull();
        registry.GetSerializer("text/xml").Should().NotBeNull();
    }

    [Fact]
    public void WithCharset_StripsParams_ReturnsSerializer()
    {
        var registry = new DataFormatRegistry();
        var s = registry.GetSerializer("application/json; charset=utf-8");
        s.Should().NotBeNull();
        s!.ContentType.Should().Be("application/json");
    }

    [Fact]
    public void StructuredSuffix_PlusJson_ReturnsJsonSerializer()
    {
        var registry = new DataFormatRegistry();
        var s = registry.GetSerializer("application/vnd.api+json");
        s.Should().NotBeNull();
        s!.ContentType.Should().Be("application/json");
    }

    [Fact]
    public void StructuredSuffix_PlusXml_ReturnsXmlSerializer()
    {
        var registry = new DataFormatRegistry();
        var s = registry.GetSerializer("application/soap+xml");
        s.Should().NotBeNull();
        s!.ContentType.Should().Be("application/xml");
    }

    [Fact]
    public void Unknown_ReturnsNull()
    {
        var registry = new DataFormatRegistry();
        registry.GetSerializer("application/octet-stream").Should().BeNull();
        registry.GetSerializer("text/plain").Should().BeNull();
    }

    [Fact]
    public void CaseInsensitive()
    {
        var registry = new DataFormatRegistry();
        registry.GetSerializer("APPLICATION/JSON").Should().NotBeNull();
    }

    [Fact]
    public void CustomRegistration_Overrides()
    {
        var registry = new DataFormatRegistry();
        var custom = new JsonMessageSerializer(); // reuse for test
        registry.Register("application/msgpack", custom);
        registry.GetSerializer("application/msgpack").Should().BeSameAs(custom);
    }
}
