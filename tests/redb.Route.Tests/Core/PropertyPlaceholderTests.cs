using FluentAssertions;
using Microsoft.Extensions.Configuration;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// Tests for the Camel-style property-placeholder feature: {{key}} / {{key:default}} expansion in
/// endpoint URIs, resolved from IConfiguration and context properties at compile time.
/// </summary>
public class PropertyPlaceholderTests
{
    // ── Pure resolver ───────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_ReplacesKey_FromLookup()
    {
        var result = PropertyPlaceholderResolver.Resolve(
            "http://{{host}}/api", k => k == "host" ? "prod-host" : null);

        result.Should().Be("http://prod-host/api");
    }

    [Fact]
    public void Resolve_UsesDefault_WhenKeyMissing()
    {
        var result = PropertyPlaceholderResolver.Resolve(
            "amqp://broker:{{port:5672}}/orders", _ => null);

        result.Should().Be("amqp://broker:5672/orders");
    }

    [Fact]
    public void Resolve_PrefersValue_OverDefault()
    {
        var result = PropertyPlaceholderResolver.Resolve(
            "amqp://broker:{{port:5672}}", k => k == "port" ? "5673" : null);

        result.Should().Be("amqp://broker:5673");
    }

    [Fact]
    public void Resolve_DefaultMayContainColon()
    {
        // A URL as a default value must survive the ':' key/default separator.
        var result = PropertyPlaceholderResolver.Resolve(
            "{{endpoint:http://localhost:8080/x}}", _ => null);

        result.Should().Be("http://localhost:8080/x");
    }

    [Fact]
    public void Resolve_ExpandsMultiplePlaceholders()
    {
        var result = PropertyPlaceholderResolver.Resolve(
            "{{scheme}}://{{host}}:{{port}}/{{queue}}",
            k => k switch { "scheme" => "amqp", "host" => "h", "port" => "5672", "queue" => "orders", _ => null });

        result.Should().Be("amqp://h:5672/orders");
    }

    [Fact]
    public void Resolve_ToleratesWhitespace()
    {
        PropertyPlaceholderResolver.Resolve("x/{{ host }}", k => k == "host" ? "h" : null)
            .Should().Be("x/h");
        PropertyPlaceholderResolver.Resolve("x/{{ port : 9 }}", _ => null)
            .Should().Be("x/9");
    }

    [Fact]
    public void Resolve_NoPlaceholder_ReturnsUnchanged()
    {
        const string uri = "direct://plain";
        PropertyPlaceholderResolver.Resolve(uri, _ => throw new InvalidOperationException("lookup must not run"))
            .Should().BeSameAs(uri);
    }

    [Fact]
    public void Resolve_MissingKeyNoDefault_ThrowsFailFast()
    {
        var act = () => PropertyPlaceholderResolver.Resolve("amqp://{{missing}}/x", _ => null);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*missing*");
    }

    [Fact]
    public void HasPlaceholder_DetectsBraces()
    {
        PropertyPlaceholderResolver.HasPlaceholder("a{{b}}c").Should().BeTrue();
        PropertyPlaceholderResolver.HasPlaceholder("plain").Should().BeFalse();
        PropertyPlaceholderResolver.HasPlaceholder(null).Should().BeFalse();
    }

    // ── Integration through RouteContext.GetEndpoint ─────────────────────────────

    [Fact]
    public void GetEndpoint_ResolvesFromContextProperty()
    {
        using var context = new RouteContext();
        context.SetProperty("queue", "orders");

        // The resolved URI must land on the same normalized endpoint as the literal form.
        var viaPlaceholder = context.GetEndpoint("direct://{{queue}}");
        var viaLiteral = context.GetEndpoint("direct://orders");

        viaPlaceholder.Should().BeSameAs(viaLiteral);
    }

    [Fact]
    public void GetEndpoint_ResolvesFromConfiguration()
    {
        using var context = new RouteContext();
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["queue"] = "invoices" })
            .Build();
        context.AddService(typeof(IConfiguration), config);

        context.GetEndpoint("direct://{{queue}}")
            .Should().BeSameAs(context.GetEndpoint("direct://invoices"));
    }

    [Fact]
    public void GetEndpoint_ConfigurationWinsOverContextProperty()
    {
        using var context = new RouteContext();
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["queue"] = "from-config" })
            .Build();
        context.AddService(typeof(IConfiguration), config);
        context.SetProperty("queue", "from-property");

        context.GetEndpoint("direct://{{queue}}")
            .Should().BeSameAs(context.GetEndpoint("direct://from-config"));
    }

    [Fact]
    public void GetEndpoint_UnresolvedPlaceholder_Throws()
    {
        using var context = new RouteContext();

        var act = () => context.GetEndpoint("direct://{{never-set}}");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*never-set*");
    }
}
