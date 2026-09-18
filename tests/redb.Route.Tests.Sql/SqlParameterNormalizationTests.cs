using System.Text.Json;
using System.Text.Json.Nodes;
using redb.Route.Sql;

namespace redb.Route.Tests.Sql;

/// <summary>JSON values become values a provider can bind: scalars as CLR values, objects and arrays as JSON text.</summary>
public sealed class SqlParameterNormalizationTests
{
    [Fact]
    public void JsonElement_Numbers_LongThenDecimalThenDouble()
    {
        SqlParameterBinder.NormalizeForDb(Element("42")).Should().Be(42L);
        SqlParameterBinder.NormalizeForDb(Element("1.5")).Should().Be(1.5m);
        SqlParameterBinder.NormalizeForDb(Element("1e300")).Should().Be(1e300);
    }

    [Fact]
    public void JsonElement_StringBoolNull()
    {
        SqlParameterBinder.NormalizeForDb(Element("\"text\"")).Should().Be("text");
        SqlParameterBinder.NormalizeForDb(Element("\"\"")).Should().Be(DBNull.Value);
        SqlParameterBinder.NormalizeForDb(Element("true")).Should().Be(true);
        SqlParameterBinder.NormalizeForDb(Element("false")).Should().Be(false);
        SqlParameterBinder.NormalizeForDb(Element("null")).Should().Be(DBNull.Value);
    }

    [Fact]
    public void JsonNode_ParsedScalars_SameAsJsonElement()
    {
        SqlParameterBinder.NormalizeForDb(JsonNode.Parse("12")).Should().Be(12L);
        SqlParameterBinder.NormalizeForDb(JsonNode.Parse("\"a\"")).Should().Be("a");
        SqlParameterBinder.NormalizeForDb(JsonNode.Parse("true")).Should().Be(true);
    }

    [Fact]
    public void JsonNode_CreatedFromClrValue_IsThatValue()
    {
        SqlParameterBinder.NormalizeForDb(JsonValue.Create(5)).Should().Be(5);
        SqlParameterBinder.NormalizeForDb(JsonValue.Create("x")).Should().Be("x");
        SqlParameterBinder.NormalizeForDb(JsonValue.Create("")).Should().Be(DBNull.Value);
    }

    [Fact]
    public void JsonNode_ObjectAndArray_AreJsonTextWithoutEscapingLetters()
    {
        SqlParameterBinder.NormalizeForDb(JsonNode.Parse("""{"a":"я"}""")).Should().Be("""{"a":"я"}""");
        SqlParameterBinder.NormalizeForDb(JsonNode.Parse("[1,2]")).Should().Be("[1,2]");
    }

    private static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
