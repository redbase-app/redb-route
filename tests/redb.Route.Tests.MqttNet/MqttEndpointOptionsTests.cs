using redb.Route.Core;
using redb.Route.MqttNet;

namespace redb.Route.Tests.MqttNet;

public class MqttEndpointOptionsTests
{
    // ── BindFromUri ─────────────────────────────────────────────────

    [Fact]
    public void BindFromUri_Mode_Parsed()
    {
        var options = new MqttEndpointOptions();
        options.BindFromUri(new Dictionary<string, string> { ["mode"] = "Publish", ["broker"] = "main" });
        options.Mode.Should().Be(MqttMode.Publish);
    }

    [Fact]
    public void BindFromUri_Subscribe_IsDefault()
    {
        var options = new MqttEndpointOptions();
        options.BindFromUri(new Dictionary<string, string> { ["broker"] = "main" });
        options.Mode.Should().Be(MqttMode.Subscribe);
    }

    [Fact]
    public void BindFromUri_Broker_Parsed()
    {
        var options = new MqttEndpointOptions();
        options.BindFromUri(new Dictionary<string, string> { ["broker"] = "production" });
        options.Broker.Should().Be("production");
    }

    [Fact]
    public void BindFromUri_Server_Parsed()
    {
        var options = new MqttEndpointOptions();
        options.BindFromUri(new Dictionary<string, string> { ["server"] = "mqtt.local" });
        options.Server.Should().Be("mqtt.local");
    }

    [Fact]
    public void BindFromUri_Port_Parsed()
    {
        var options = new MqttEndpointOptions();
        options.BindFromUri(new Dictionary<string, string> { ["server"] = "x", ["port"] = "8883" });
        options.Port.Should().Be(8883);
    }

    [Fact]
    public void BindFromUri_Qos_Parsed()
    {
        var options = new MqttEndpointOptions();
        options.BindFromUri(new Dictionary<string, string> { ["broker"] = "m", ["qos"] = "2" });
        options.Qos.Should().Be(2);
    }

    [Fact]
    public void BindFromUri_UseTls_Parsed()
    {
        var options = new MqttEndpointOptions();
        options.BindFromUri(new Dictionary<string, string> { ["broker"] = "m", ["useTls"] = "true" });
        options.UseTls.Should().BeTrue();
    }

    [Fact]
    public void BindFromUri_CleanSession_Parsed()
    {
        var options = new MqttEndpointOptions();
        options.BindFromUri(new Dictionary<string, string> { ["broker"] = "m", ["cleanSession"] = "false" });
        options.CleanSession.Should().BeFalse();
    }

    [Fact]
    public void BindFromUri_KeepAlive_Parsed()
    {
        var options = new MqttEndpointOptions();
        options.BindFromUri(new Dictionary<string, string> { ["broker"] = "m", ["keepAlive"] = "30" });
        options.KeepAlive.Should().Be(30);
    }

    [Fact]
    public void BindFromUri_Retain_Parsed()
    {
        var options = new MqttEndpointOptions();
        options.BindFromUri(new Dictionary<string, string> { ["broker"] = "m", ["retain"] = "true" });
        options.Retain.Should().BeTrue();
    }

    [Fact]
    public void BindFromUri_SharedSubscription_Parsed()
    {
        var options = new MqttEndpointOptions();
        options.BindFromUri(new Dictionary<string, string> { ["broker"] = "m", ["sharedSubscription"] = "grp" });
        options.SharedSubscription.Should().Be("grp");
    }

    [Fact]
    public void BindFromUri_MessageExpiryInterval_Parsed()
    {
        var options = new MqttEndpointOptions();
        options.BindFromUri(new Dictionary<string, string> { ["broker"] = "m", ["messageExpiryInterval"] = "600" });
        options.MessageExpiryInterval.Should().Be(600);
    }

    [Fact]
    public void BindFromUri_ContentType_Parsed()
    {
        var options = new MqttEndpointOptions();
        options.BindFromUri(new Dictionary<string, string> { ["broker"] = "m", ["contentType"] = "application/json" });
        options.ContentType.Should().Be("application/json");
    }

    [Fact]
    public void BindFromUri_ResponseTopic_Parsed()
    {
        var options = new MqttEndpointOptions();
        options.BindFromUri(new Dictionary<string, string> { ["broker"] = "m", ["responseTopic"] = "resp/t" });
        options.ResponseTopic.Should().Be("resp/t");
    }

    [Fact]
    public void BindFromUri_Username_Parsed()
    {
        var options = new MqttEndpointOptions();
        options.BindFromUri(new Dictionary<string, string> { ["server"] = "x", ["username"] = "admin" });
        options.Username.Should().Be("admin");
    }

    [Fact]
    public void BindFromUri_Password_Parsed()
    {
        var options = new MqttEndpointOptions();
        options.BindFromUri(new Dictionary<string, string> { ["server"] = "x", ["password"] = "secret" });
        options.Password.Should().Be("secret");
    }

    [Fact]
    public void BindFromUri_ClientId_Parsed()
    {
        var options = new MqttEndpointOptions();
        options.BindFromUri(new Dictionary<string, string> { ["broker"] = "m", ["clientId"] = "myClient" });
        options.ClientId.Should().Be("myClient");
    }

    // ── Validate ────────────────────────────────────────────────────

    [Fact]
    public void Validate_NoBrokerNoServer_Throws()
    {
        var options = new MqttEndpointOptions();
        var act = () => options.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*Broker*Server*");
    }

    [Fact]
    public void Validate_WithBroker_DoesNotThrow()
    {
        var options = new MqttEndpointOptions { Broker = "main" };
        var act = () => options.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_WithServer_DoesNotThrow()
    {
        var options = new MqttEndpointOptions { Server = "localhost" };
        var act = () => options.Validate();
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(99)]
    public void Validate_InvalidQos_Throws(int qos)
    {
        var options = new MqttEndpointOptions { Broker = "main", Qos = qos };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*QoS*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Validate_ValidQos_DoesNotThrow(int qos)
    {
        var options = new MqttEndpointOptions { Broker = "main", Qos = qos };
        var act = () => options.Validate();
        act.Should().NotThrow();
    }

    // ── Defaults ────────────────────────────────────────────────────

    [Fact]
    public void DefaultMode_IsSubscribe()
    {
        new MqttEndpointOptions().Mode.Should().Be(MqttMode.Subscribe);
    }

    [Fact]
    public void DefaultQos_IsZero()
    {
        new MqttEndpointOptions().Qos.Should().Be(0);
    }

    [Fact]
    public void DefaultKeepAlive_Is60()
    {
        new MqttEndpointOptions().KeepAlive.Should().Be(60);
    }

    [Fact]
    public void DefaultCleanSession_IsTrue()
    {
        new MqttEndpointOptions().CleanSession.Should().BeTrue();
    }

    [Fact]
    public void DefaultRetain_IsFalse()
    {
        new MqttEndpointOptions().Retain.Should().BeFalse();
    }

    [Fact]
    public void DefaultMessageExpiryInterval_IsZero()
    {
        new MqttEndpointOptions().MessageExpiryInterval.Should().Be(0);
    }

    [Fact]
    public void DefaultPort_IsZero()
    {
        new MqttEndpointOptions().Port.Should().Be(0);
    }

    [Fact]
    public void DefaultUseTls_IsFalse()
    {
        new MqttEndpointOptions().UseTls.Should().BeFalse();
    }
}
