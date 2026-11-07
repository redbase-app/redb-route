using redb.Route.Serialization;

namespace redb.Route.Tests.Serialization;

/// <summary>
/// Tests for <see cref="JsonMessageSerializer"/>.
/// </summary>
public class JsonMessageSerializerTests
{
    private readonly JsonMessageSerializer _sut = new();

    [Fact]
    public void ContentType_IsApplicationJson()
    {
        _sut.ContentType.Should().Be("application/json");
    }

    [Fact]
    public void Serialize_ReturnsNonEmptyBytes()
    {
        var bytes = _sut.Serialize(new { Name = "test", Value = 42 });

        bytes.Should().NotBeEmpty();
    }

    [Fact]
    public void Roundtrip_PreservesObject()
    {
        var original = new TestDto("Alice", 30);

        var bytes = _sut.Serialize(original);
        var restored = _sut.Deserialize<TestDto>(bytes);

        restored.Should().NotBeNull();
        restored!.Name.Should().Be("Alice");
        restored.Age.Should().Be(30);
    }

    [Fact]
    public void Deserialize_UntypedOverload_ReturnsCorrectType()
    {
        var original = new TestDto("Bob", 25);

        var bytes = _sut.Serialize(original);
        var restored = _sut.Deserialize(bytes, typeof(TestDto));

        restored.Should().BeOfType<TestDto>();
        ((TestDto)restored!).Name.Should().Be("Bob");
    }

    [Fact]
    public void Serialize_UsesCamelCase()
    {
        var obj = new TestDto("Test", 1);

        var bytes = _sut.Serialize(obj);
        var json = System.Text.Encoding.UTF8.GetString(bytes);

        json.Should().Contain("\"name\"");
        json.Should().Contain("\"age\"");
        json.Should().NotContain("\"Name\"");
    }

    [Fact]
    public void Deserialize_IsCaseInsensitive()
    {
        var json = """{"Name":"Case","Age":10}"""u8.ToArray();

        var result = _sut.Deserialize<TestDto>(json);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Case");
        result.Age.Should().Be(10);
    }

    [Fact]
    public void Serialize_IgnoresNullValues()
    {
        var obj = new NullableDto { Name = "test", Description = null };

        var bytes = _sut.Serialize(obj);
        var json = System.Text.Encoding.UTF8.GetString(bytes);

        json.Should().NotContain("description");
    }

    public record TestDto(string Name, int Age);

    public class NullableDto
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
    }
}
