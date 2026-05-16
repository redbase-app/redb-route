using redb.Route.S3;

namespace redb.Route.Tests.S3;

public sealed class S3HeadersTests
{
    [Fact]
    public void AllHeaders_HaveCorrectPrefix()
    {
        // All public const string fields should start with prefix "redbS3."
        var fields = typeof(S3Headers)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string) && f.Name != "MetadataPrefix")
            .Select(f => (f.Name, Value: (string)f.GetValue(null)!))
            .ToList();

        fields.Should().NotBeEmpty();
        foreach (var (name, value) in fields)
        {
            value.Should().StartWith("redbS3.", because: $"header {name} must have the S3 prefix");
        }
    }

    [Fact]
    public void MetadataPrefix_EndsWithDot()
    {
        S3Headers.MetadataPrefix.Should().EndWith(".");
    }

    [Fact]
    public void IsRedbHeader_ReturnsTrueForKnownHeaders()
    {
        S3Headers.IsRedbHeader(S3Headers.BucketName).Should().BeTrue();
        S3Headers.IsRedbHeader(S3Headers.Key).Should().BeTrue();
        S3Headers.IsRedbHeader(S3Headers.ETag).Should().BeTrue();
    }

    [Fact]
    public void IsRedbHeader_ReturnsFalseForUnknownHeaders()
    {
        S3Headers.IsRedbHeader("Content-Type").Should().BeFalse();
        S3Headers.IsRedbHeader("unknown").Should().BeFalse();
    }

    [Fact]
    public void HeaderValues_AreUnique()
    {
        var fields = typeof(S3Headers)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string) && f.Name != "MetadataPrefix")
            .Select(f => (string)f.GetValue(null)!)
            .ToList();

        fields.Should().OnlyHaveUniqueItems();
    }
}
