using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// Code review 2026-09-01 (the UriMask query item): a mask without a query string ignores the
/// candidate's query; a mask that carries one matches only that query. Before, both sides were
/// query-stripped, so <c>kafka://orders?acks=all</c> matched <c>?acks=none</c> too.
/// </summary>
public class UriMaskReviewTests
{
    [Theory]
    [InlineData("kafka://orders", "kafka://orders?acks=all", true)]
    [InlineData("kafka://orders?acks=all", "kafka://orders?acks=all", true)]
    [InlineData("kafka://orders?acks=all", "kafka://orders?acks=none", false)]
    [InlineData("kafka://orders?acks=all", "kafka://orders", false)]
    [InlineData("kafka:orders?acks=all", "kafka://orders?acks=all", true)]
    public void ExactMask_QueryIsSignificantOnlyWhenTheMaskCarriesOne(string pattern, string uri, bool expected)
        => UriMask.IsMatch(pattern, uri).Should().Be(expected);

    [Theory]
    [InlineData("kafka://orders?acks=*", "kafka://orders?acks=all", true)]
    [InlineData("kafka://orders?acks=*", "kafka://orders?retries=3", false)]
    [InlineData("kafka://*", "kafka://orders?acks=all", true)]
    [InlineData("kafka:orders*", "kafka://orders-vip", true)]
    public void PrefixMask_QueryIsSignificantOnlyWhenTheMaskCarriesOne(string pattern, string uri, bool expected)
        => UriMask.IsMatch(pattern, uri).Should().Be(expected);

    [Theory]
    [InlineData("regex:^kafka:orders$", "kafka://orders?acks=all", true)]
    [InlineData("regex:ORDERS", "kafka://orders", true)]
    public void RegexMask_MatchesTheKnownSpellings(string pattern, string uri, bool expected)
        => UriMask.IsMatch(pattern, uri).Should().Be(expected);
}
