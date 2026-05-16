using redb.Route.Firebase;

namespace redb.Route.Tests.Firebase;

public sealed class FirebaseStorageHeadersTests
{
    [Fact]
    public void Prefix_IsCorrect()
    {
        FirebaseStorageHeaders.Prefix.Should().Be("redbStorage.");
    }

    [Theory]
    [InlineData(nameof(FirebaseStorageHeaders.BucketName), "redbStorage.BucketName")]
    [InlineData(nameof(FirebaseStorageHeaders.ObjectName), "redbStorage.ObjectName")]
    [InlineData(nameof(FirebaseStorageHeaders.ContentType), "redbStorage.ContentType")]
    [InlineData(nameof(FirebaseStorageHeaders.MediaLink), "redbStorage.MediaLink")]
    [InlineData(nameof(FirebaseStorageHeaders.MetadataPrefix), "redbStorage.Meta.")]
    public void Header_StartsWithPrefix(string fieldName, string expected)
    {
        var value = typeof(FirebaseStorageHeaders).GetField(fieldName)?.GetValue(null) as string;
        value.Should().NotBeNull();
        value.Should().StartWith(FirebaseStorageHeaders.Prefix);
        value.Should().Be(expected);
    }
}
