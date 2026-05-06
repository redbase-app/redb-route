using redb.Route.Firebase;

namespace redb.Route.Tests.Firebase;

public sealed class FirestoreHeadersTests
{
    [Fact]
    public void Prefix_IsCorrect()
    {
        FirestoreHeaders.Prefix.Should().Be("redbFirestore.");
    }

    [Theory]
    [InlineData(nameof(FirestoreHeaders.DocumentId), "redbFirestore.DocumentId")]
    [InlineData(nameof(FirestoreHeaders.DocumentPath), "redbFirestore.DocumentPath")]
    [InlineData(nameof(FirestoreHeaders.CollectionPath), "redbFirestore.CollectionPath")]
    [InlineData(nameof(FirestoreHeaders.ChangeType), "redbFirestore.ChangeType")]
    public void Header_StartsWithPrefix(string fieldName, string expected)
    {
        var value = typeof(FirestoreHeaders).GetField(fieldName)?.GetValue(null) as string;
        value.Should().NotBeNull();
        value.Should().StartWith(FirestoreHeaders.Prefix);
        value.Should().Be(expected);
    }
}
