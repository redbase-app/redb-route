using FluentAssertions;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// A typed read of a header, a property or a received body converts as Camel does: a missing value, or one with no
/// conversion to the type at all, reads as default; a value the conversion exists for but cannot parse ("abc" as an
/// int) is an error naming the key and the type, never its value. It used to read as default too, so a typo in the
/// data sent the route down its "no value" branch without a word. A nullable target converts like its underlying type.
/// </summary>
public class TypedReadConversionTests
{
    private sealed class Customer;

    private static Message Headers(params (string Key, object? Value)[] headers)
    {
        var message = new Message("body");
        foreach (var (key, value) in headers)
            message.Headers[key] = value;
        return message;
    }

    [Fact]
    public void A_header_that_cannot_be_parsed_as_the_type_is_an_error_naming_it()
    {
        var message = Headers(("count", "abc"));

        var act = () => message.GetHeader<int>("count");

        act.Should().Throw<FormatException>()
            .WithMessage("*'count'*Int32*")
            .Which.Message.Should().NotContain("abc");
    }

    [Fact]
    public void A_missing_header_reads_as_default()
        => Headers().GetHeader<int>("count").Should().Be(0);

    [Fact]
    public void A_header_with_no_conversion_to_the_type_reads_as_default()
        => Headers(("customer", "acme")).GetHeader<Customer>("customer").Should().BeNull();

    [Fact]
    public void A_nullable_target_converts_like_its_underlying_type()
        => Headers(("count", "5")).GetHeader<int?>("count").Should().Be(5);

    [Fact]
    public void Numbers_are_read_in_the_invariant_culture()
        => Headers(("price", "1.5")).GetHeader<decimal>("price").Should().Be(1.5m);

    [Fact]
    public void A_property_follows_the_same_rule()
    {
        var exchange = new Exchange(new Message("body"));
        exchange.Properties["retries"] = "many";

        var act = () => exchange.GetProperty<int>("retries");

        act.Should().Throw<FormatException>().WithMessage("*'retries'*");
        exchange.GetProperty<int>("missing").Should().Be(0);
    }
}
