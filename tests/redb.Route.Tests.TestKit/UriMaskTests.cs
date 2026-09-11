using redb.Route.Core;

namespace redb.Route.Tests.TestKit;

public class UriMaskTests
{
    [Theory]
    [InlineData("kafka://orders", "kafka://orders", true)]
    [InlineData("kafka://orders", "kafka://orders?acks=all", true)]
    [InlineData("KAFKA://ORDERS", "kafka://orders", true)]
    [InlineData("kafka:orders", "kafka://orders", true)]
    [InlineData("kafka://orders", "kafka://orders-vip", false)]
    [InlineData("kafka://*", "kafka://orders-vip?acks=all", true)]
    [InlineData("kafka://*", "sql:select 1", false)]
    [InlineData("sql:*", "sql:INSERT INTO audit", true)]
    [InlineData("kafka://orders-*", "kafka://orders-vip", true)]
    [InlineData("regex:^kafka://orders-(vip|std)$", "kafka://orders-std", true)]
    [InlineData("regex:^kafka://orders-(vip|std)$", "kafka://orders-x", false)]
    [InlineData("regex:^kafka://orders-(vip|std)$", "kafka://orders-vip?acks=all", true)]
    public void IsMatch(string pattern, string uri, bool expected)
        => UriMask.IsMatch(pattern, uri).Should().Be(expected);

    [Fact]
    public void EmptyArguments_AreRejected()
    {
        var act = () => UriMask.IsMatch("", "kafka://x");
        act.Should().Throw<ArgumentException>();
    }
}
