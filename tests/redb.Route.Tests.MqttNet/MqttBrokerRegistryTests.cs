using redb.Route.MqttNet.Connection;

namespace redb.Route.Tests.MqttNet;

public class MqttBrokerRegistryTests
{
    [Fact]
    public void Register_AndGetOptions_ReturnsSameOptions()
    {
        var registry = new MqttBrokerRegistry();
        var options = new MqttBrokerOptions { Server = "broker1.local", Port = 1883 };
        registry.Register("prod", options);

        var result = registry.GetOptions("prod");

        result.Should().BeSameAs(options);
    }

    [Fact]
    public void Contains_RegisteredBroker_ReturnsTrue()
    {
        var registry = new MqttBrokerRegistry();
        registry.Register("main", new MqttBrokerOptions());

        registry.Contains("main").Should().BeTrue();
    }

    [Fact]
    public void Contains_UnregisteredBroker_ReturnsFalse()
    {
        var registry = new MqttBrokerRegistry();

        registry.Contains("unknown").Should().BeFalse();
    }

    [Fact]
    public void GetOptions_UnregisteredBroker_Throws()
    {
        var registry = new MqttBrokerRegistry();

        var act = () => registry.GetOptions("missing");

        act.Should().Throw<InvalidOperationException>().WithMessage("*'missing'*not registered*");
    }

    [Fact]
    public void Register_CaseInsensitive()
    {
        var registry = new MqttBrokerRegistry();
        registry.Register("Main", new MqttBrokerOptions { Server = "a" });

        registry.Contains("main").Should().BeTrue();
        registry.Contains("MAIN").Should().BeTrue();
        registry.GetOptions("main").Server.Should().Be("a");
    }

    [Fact]
    public void Register_OverwritesExisting()
    {
        var registry = new MqttBrokerRegistry();
        registry.Register("main", new MqttBrokerOptions { Server = "old" });
        registry.Register("main", new MqttBrokerOptions { Server = "new" });

        registry.GetOptions("main").Server.Should().Be("new");
    }

    [Fact]
    public void Register_NullName_Throws()
    {
        var registry = new MqttBrokerRegistry();

        var act = () => registry.Register(null!, new MqttBrokerOptions());

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Register_EmptyName_Throws()
    {
        var registry = new MqttBrokerRegistry();

        var act = () => registry.Register("", new MqttBrokerOptions());

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Register_NullOptions_Throws()
    {
        var registry = new MqttBrokerRegistry();

        var act = () => registry.Register("main", null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void MultipleBrokers_IndependentLookup()
    {
        var registry = new MqttBrokerRegistry();
        registry.Register("prod", new MqttBrokerOptions { Server = "prod.broker" });
        registry.Register("dev", new MqttBrokerOptions { Server = "dev.broker" });

        registry.GetOptions("prod").Server.Should().Be("prod.broker");
        registry.GetOptions("dev").Server.Should().Be("dev.broker");
    }
}

public class MqttBrokerOptionsTests
{
    [Fact]
    public void DefaultServer_IsLocalhost()
    {
        new MqttBrokerOptions().Server.Should().Be("localhost");
    }

    [Fact]
    public void DefaultPort_Is1883()
    {
        new MqttBrokerOptions().Port.Should().Be(1883);
    }

    [Fact]
    public void DefaultKeepAlive_Is60()
    {
        new MqttBrokerOptions().KeepAlive.Should().Be(60);
    }

    [Fact]
    public void DefaultCleanSession_IsTrue()
    {
        new MqttBrokerOptions().CleanSession.Should().BeTrue();
    }

    [Fact]
    public void DefaultUseTls_IsFalse()
    {
        new MqttBrokerOptions().UseTls.Should().BeFalse();
    }
}
