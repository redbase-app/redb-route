using redb.Route.Core;
using FluentAssertions;

namespace redb.Route.Tests.Core;

public class UriParserTests
{
    [Theory]
    [InlineData("kafka://orders", "kafka", "orders", "kafka://orders")]
    [InlineData("rabbitmq://exchange/queue", "rabbitmq", "exchange/queue", "rabbitmq://exchange/queue")]
    [InlineData("direct://start", "direct", "start", "direct://start")]
    [InlineData("seda://buffer", "seda", "buffer", "seda://buffer")]
    [InlineData("timer://heartbeat", "timer", "heartbeat", "timer://heartbeat")]
    public void Parse_StandardFormat_ExtractsComponents(string uri, string scheme, string path, string key)
    {
        var result = EndpointUriParser.Parse(uri);

        result.Scheme.Should().Be(scheme);
        result.Path.Should().Be(path);
        result.NormalizedKey.Should().Be(key);
    }

    [Theory]
    [InlineData("redis:GET:user:123", "redis", "GET:user:123", "redis://GET:user:123")]
    [InlineData("redis:SET:cache:key", "redis", "SET:cache:key", "redis://SET:cache:key")]
    public void Parse_ColonFormat_ExtractsComponents(string uri, string scheme, string path, string key)
    {
        var result = EndpointUriParser.Parse(uri);

        result.Scheme.Should().Be(scheme);
        result.Path.Should().Be(path);
        result.NormalizedKey.Should().Be(key);
    }

    [Fact]
    public void Parse_WithQueryParams_ExtractsParameters()
    {
        var result = EndpointUriParser.Parse("kafka://orders?brokers=localhost:9092&groupId=my-group");

        result.Scheme.Should().Be("kafka");
        result.Path.Should().Be("orders");
        result.NormalizedKey.Should().Be("kafka://orders?brokers=localhost:9092&groupId=my-group");
        result.RawParameters.Should().HaveCount(2);
        result.RawParameters["brokers"].Should().Be("localhost:9092");
        result.RawParameters["groupId"].Should().Be("my-group");
    }

    [Fact]
    public void Parse_ColonWithParams_Works()
    {
        var result = EndpointUriParser.Parse("redis:GET:user:123?ttl=60");

        result.Scheme.Should().Be("redis");
        result.Path.Should().Be("GET:user:123");
        result.RawParameters["ttl"].Should().Be("60");
    }

    [Fact]
    public void Parse_NoParams_EmptyDictionary()
    {
        var result = EndpointUriParser.Parse("direct://start");
        result.RawParameters.Should().BeEmpty();
    }

    [Fact]
    public void Parse_SchemeIsCaseInsensitive()
    {
        var result = EndpointUriParser.Parse("KAFKA://orders");
        result.Scheme.Should().Be("kafka");
    }

    [Fact]
    public void Parse_ParametersAreCaseInsensitive()
    {
        var result = EndpointUriParser.Parse("kafka://t?BrokERS=host");
        result.RawParameters["brokers"].Should().Be("host");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Parse_EmptyOrNull_Throws(string? uri)
    {
        var act = () => EndpointUriParser.Parse(uri!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_NoScheme_Throws()
    {
        var act = () => EndpointUriParser.Parse("orders");
        act.Should().Throw<ArgumentException>();
    }

    /// <summary>Flag parameters (no =value) should have empty string as value.</summary>
    [Fact]
    public void Parse_FlagParameter_EmptyValue()
    {
        var result = EndpointUriParser.Parse("rabbitmq://orders?transacted");
        result.RawParameters["transacted"].Should().Be(string.Empty);
    }

    /// <summary>Dynamic expressions like ${header.tripId} must be preserved as-is in parameters.</summary>
    [Fact]
    public void Parse_DynamicExpression_PreservedInParams()
    {
        var result = EndpointUriParser.Parse("kafka://orders?key=${header.tripId}");
        result.RawParameters["key"].Should().Be("${header.tripId}");
    }
}
