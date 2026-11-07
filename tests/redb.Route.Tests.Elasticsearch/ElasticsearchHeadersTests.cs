using redb.Route.Elasticsearch;

namespace redb.Route.Tests.Elasticsearch;

public sealed class ElasticsearchHeadersTests
{
    [Fact]
    public void AllHeaders_HaveCorrectPrefix()
    {
        var fields = typeof(ElasticsearchHeaders)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string) && f.Name != "Prefix")
            .Select(f => (f.Name, Value: (string)f.GetValue(null)!))
            .ToList();

        fields.Should().NotBeEmpty();
        foreach (var (name, value) in fields)
        {
            value.Should().StartWith("redbEs.", because: $"header {name} must have the ES prefix");
        }
    }

    [Fact]
    public void IsRedbHeader_ReturnsTrueForKnownHeaders()
    {
        ElasticsearchHeaders.IsRedbHeader(ElasticsearchHeaders.IndexName).Should().BeTrue();
        ElasticsearchHeaders.IsRedbHeader(ElasticsearchHeaders.DocumentId).Should().BeTrue();
        ElasticsearchHeaders.IsRedbHeader(ElasticsearchHeaders.Version).Should().BeTrue();
        ElasticsearchHeaders.IsRedbHeader(ElasticsearchHeaders.Score).Should().BeTrue();
    }

    [Fact]
    public void IsRedbHeader_ReturnsFalseForUnknownHeaders()
    {
        ElasticsearchHeaders.IsRedbHeader("Content-Type").Should().BeFalse();
        ElasticsearchHeaders.IsRedbHeader("unknown").Should().BeFalse();
        ElasticsearchHeaders.IsRedbHeader("redbS3.Key").Should().BeFalse();
    }

    [Fact]
    public void HeaderValues_AreUnique()
    {
        var fields = typeof(ElasticsearchHeaders)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string) && f.Name != "Prefix")
            .Select(f => (string)f.GetValue(null)!)
            .ToList();

        fields.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Prefix_IsRedbEs()
    {
        ElasticsearchHeaders.Prefix.Should().Be("redbEs.");
    }
}
