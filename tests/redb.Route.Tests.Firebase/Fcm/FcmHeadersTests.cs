using redb.Route.Firebase;

namespace redb.Route.Tests.Firebase;

public sealed class FcmHeadersTests
{
    [Fact]
    public void Prefix_IsCorrect()
    {
        FcmHeaders.Prefix.Should().Be("redbFcm.");
    }

    [Theory]
    [InlineData(nameof(FcmHeaders.MessageId), "redbFcm.MessageId")]
    [InlineData(nameof(FcmHeaders.Token), "redbFcm.Token")]
    [InlineData(nameof(FcmHeaders.Topic), "redbFcm.Topic")]
    [InlineData(nameof(FcmHeaders.Condition), "redbFcm.Condition")]
    public void Header_StartsWithPrefix(string fieldName, string expected)
    {
        var value = typeof(FcmHeaders).GetField(fieldName)?.GetValue(null) as string;
        value.Should().NotBeNull();
        value.Should().StartWith(FcmHeaders.Prefix);
        value.Should().Be(expected);
    }
}
