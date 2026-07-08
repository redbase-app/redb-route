using redb.Route.Core;
using FluentAssertions;

namespace redb.Route.Tests.Core;

// Test options class for binding tests
public class TestEndpointOptions : EndpointOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 5672;
    public bool Durable { get; set; } = true;
    public DynamicValue<string>? Key { get; set; }
    public DynamicValue<int>? Ttl { get; set; }

    public override void Validate()
    {
        if (string.IsNullOrEmpty(Host))
            throw new InvalidOperationException("Host is required");
    }
}

public class EndpointOptionsBindingTests
{
    [Fact]
    public void BindFromUri_MapsStringProperty()
    {
        var opts = new TestEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string> { ["host"] = "rabbit1" });
        opts.Host.Should().Be("rabbit1");
    }

    [Fact]
    public void BindFromUri_MapsIntProperty()
    {
        var opts = new TestEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string> { ["port"] = "9999" });
        opts.Port.Should().Be(9999);
    }

    [Fact]
    public void BindFromUri_MapsBoolProperty()
    {
        var opts = new TestEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string> { ["durable"] = "false" });
        opts.Durable.Should().BeFalse();
    }

    [Fact]
    public void BindFromUri_IsCaseInsensitive()
    {
        var opts = new TestEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string> { ["HOST"] = "server" });
        opts.Host.Should().Be("server");
    }

    [Fact]
    public void BindFromUri_UnmappedParams_GoToUnmapped()
    {
        var opts = new TestEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string>
        {
            ["host"] = "server",
            ["unknownParam"] = "value"
        });

        opts.Host.Should().Be("server");
        opts.UnmappedParameters.Should().ContainKey("unknownParam");
        opts.UnmappedParameters["unknownParam"].Should().Be("value");
    }

    [Fact]
    public void BindFromUri_StaticDynamicValue_Wraps()
    {
        var opts = new TestEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string> { ["key"] = "static-key" });

        opts.Key.Should().NotBeNull();
        opts.Key!.Value.IsDynamic.Should().BeFalse();
        opts.Key!.Value.Resolve(new Exchange()).Should().Be("static-key");
    }

    [Fact]
    public void BindFromUri_EmptyParams_NoOp()
    {
        var opts = new TestEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string>());
        opts.Host.Should().BeNull(); // default
        opts.Port.Should().Be(5672); // default
    }

    [Fact]
    public void Validate_MissingRequired_Throws()
    {
        var opts = new TestEndpointOptions();
        var act = () => opts.Validate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*Host*");
    }

    [Fact]
    public void Validate_WithHost_Passes()
    {
        var opts = new TestEndpointOptions();
        opts.Host = "localhost";
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }
}
