using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// A property whose name contains a dot (<c>omni.PollInterval</c>, the shape a module uses to
/// namespace its settings). Reported 2026-09-24: written bare — <c>property.omni.PollInterval</c> in
/// a <c>delay</c> or a <c>setProperty</c> — it read as empty, while the same name inside
/// <c>${...}</c>, in a comparison, and any dotted <b>header</b> resolved fine. The bare value path
/// went straight to nested access, so it looked for a property named <c>omni</c> and a member
/// <c>PollInterval</c> on it; every other path asks for the literal name first.
/// </summary>
[Collection("ExpressionResolver")]
public class DottedPropertyNameTests : IDisposable
{
    public DottedPropertyNameTests() => ExpressionResolver.ClearAllCaches();

    public void Dispose()
    {
        ExpressionResolver.ClearAllCaches();
        GC.SuppressFinalize(this);
    }

    private static IExchange WithProperty(string name, object? value)
    {
        var exchange = new Exchange(new Message("body"));
        exchange.Properties[name] = value;
        return exchange;
    }

    private sealed class Settings
    {
        public int PollInterval { get; set; }
    }

    [Fact]
    public void A_bare_expression_reads_a_property_whose_name_holds_a_dot()
    {
        var exchange = WithProperty("omni.PollInterval", 5000);

        new StringExpression("property.omni.PollInterval").Evaluate<object>(exchange).Should().Be(5000);
    }

    [Fact]
    public void The_template_form_reads_the_same_property()
    {
        var exchange = WithProperty("omni.PollInterval", 5000);

        // Already worked — kept so the two forms are asserted side by side and stay in step.
        new StringExpression("${property.omni.PollInterval}").Evaluate<string>(exchange).Should().Be("5000");
    }

    [Fact]
    public void A_nested_member_is_still_read_when_no_property_carries_the_literal_name()
    {
        var exchange = WithProperty("omni", new Settings { PollInterval = 700 });

        new StringExpression("property.omni.PollInterval").Evaluate<object>(exchange).Should().Be(700);
    }

    [Fact]
    public void The_literal_name_wins_over_a_nested_member_of_the_same_shape()
    {
        var exchange = WithProperty("omni", new Settings { PollInterval = 700 });
        exchange.Properties["omni.PollInterval"] = 5000;

        // The rule the smart resolver has always followed for headers and for templates: a property
        // written with that exact name is what the author meant.
        new StringExpression("property.omni.PollInterval").Evaluate<object>(exchange).Should().Be(5000);
    }

    [Fact]
    public void A_name_that_matches_nothing_is_null_rather_than_a_throw()
    {
        var exchange = WithProperty("other", 1);

        new StringExpression("property.omni.PollInterval").Evaluate<object>(exchange).Should().BeNull();
    }

    [Fact]
    public void A_dotted_header_keeps_reading_the_literal_name()
    {
        var exchange = new Exchange(new Message("body"));
        exchange.In.Headers["omni.PollInterval"] = 5000;

        new StringExpression("header.omni.PollInterval").Evaluate<object>(exchange).Should().Be(5000);
    }
}
