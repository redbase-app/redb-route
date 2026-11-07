using redb.Route.MqttNet;
using redb.Route.Expressions;

namespace redb.Route.Tests.MqttNet;

public class MqttBuilderTests
{
    private static ConstantExpression C(string s) => new(s);
    // ── Factory methods ─────────────────────────────────────────────

    [Fact]
    public void Subscribe_SetsSubscribeMode()
    {
        var uri = Mqtt.Subscribe("sensors/temp").Broker(C("main")).Build();
        uri.Should().Contain("mode=Subscribe");
    }

    [Fact]
    public void Publish_SetsPublishMode()
    {
        var uri = Mqtt.Publish("sensors/temp").Broker(C("main")).Build();
        uri.Should().Contain("mode=Publish");
    }

    [Fact]
    public void NullTopic_Throws()
    {
        var act = () => Mqtt.Subscribe(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EmptyTopic_Throws()
    {
        var act = () => Mqtt.Subscribe("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WhitespaceTopic_Throws()
    {
        var act = () => Mqtt.Publish("   ");
        act.Should().Throw<ArgumentException>();
    }

    // ── URI structure ───────────────────────────────────────────────

    [Fact]
    public void Build_StartsWithMqttScheme()
    {
        var uri = Mqtt.Subscribe("test").Broker(C("main")).Build();
        uri.Should().StartWith("mqtt:");
    }

    [Fact]
    public void Build_ContainsTopicInPath()
    {
        var uri = Mqtt.Subscribe("sensors/temperature").Broker(C("main")).Build();
        uri.Should().StartWith("mqtt:sensors/temperature");
    }

    [Fact]
    public void Build_FirstParamAfterQuestionMark()
    {
        var uri = Mqtt.Subscribe("topic").Broker(C("main")).Build();
        uri.Should().Contain("?mode=");
    }

    // ── Connection parameters ───────────────────────────────────────

    [Fact]
    public void Broker_SetsParam()
    {
        var uri = Mqtt.Subscribe("t").Broker(C("prod")).Build();
        uri.Should().Contain("broker=prod");
    }

    [Fact]
    public void Server_SetsParam()
    {
        var uri = Mqtt.Subscribe("t").Server(C("mqtt.example.com")).Build();
        uri.Should().Contain("server=mqtt.example.com");
    }

    [Fact]
    public void Port_SetsParam()
    {
        var uri = Mqtt.Subscribe("t").Server(C("localhost")).Port(8883).Build();
        uri.Should().Contain("port=8883");
    }

    [Fact]
    public void Username_SetsParam()
    {
        var uri = Mqtt.Subscribe("t").Server(C("localhost")).Username(C("admin")).Build();
        uri.Should().Contain("username=admin");
    }

    [Fact]
    public void Password_SetsParam()
    {
        var uri = Mqtt.Subscribe("t").Server(C("localhost")).Password(C("secret")).Build();
        uri.Should().Contain("password=secret");
    }

    [Fact]
    public void ClientId_SetsParam()
    {
        var uri = Mqtt.Subscribe("t").Broker(C("main")).ClientId(C("myClient")).Build();
        uri.Should().Contain("clientId=myClient");
    }

    [Fact]
    public void UseTls_SetsParam()
    {
        var uri = Mqtt.Subscribe("t").Broker(C("main")).UseTls().Build();
        uri.Should().Contain("useTls=true");
    }

    [Fact]
    public void KeepAlive_SetsParam()
    {
        var uri = Mqtt.Subscribe("t").Broker(C("main")).KeepAlive(30).Build();
        uri.Should().Contain("keepAlive=30");
    }

    [Fact]
    public void CleanSession_True_SetsParam()
    {
        var uri = Mqtt.Subscribe("t").Broker(C("main")).CleanSession(true).Build();
        uri.Should().Contain("cleanSession=true");
    }

    [Fact]
    public void CleanSession_False_SetsParam()
    {
        var uri = Mqtt.Subscribe("t").Broker(C("main")).CleanSession(false).Build();
        uri.Should().Contain("cleanSession=false");
    }

    // ── QoS ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Qos_SetsParam(int qos)
    {
        var uri = Mqtt.Subscribe("t").Broker(C("main")).Qos(qos).Build();
        uri.Should().Contain($"qos={qos}");
    }

    // ── Subscribe parameters ────────────────────────────────────────

    [Fact]
    public void SharedSubscription_SetsParam()
    {
        var uri = Mqtt.Subscribe("t").Broker(C("main")).SharedSubscription(C("group1")).Build();
        uri.Should().Contain("sharedSubscription=group1");
    }

    // ── Publish parameters ──────────────────────────────────────────

    [Fact]
    public void Retain_SetsParam()
    {
        var uri = Mqtt.Publish("t").Broker(C("main")).Retain().Build();
        uri.Should().Contain("retain=true");
    }

    [Fact]
    public void MessageExpiryInterval_SetsParam()
    {
        var uri = Mqtt.Publish("t").Broker(C("main")).MessageExpiryInterval(3600).Build();
        uri.Should().Contain("messageExpiryInterval=3600");
    }

    [Fact]
    public void ContentType_SetsParam()
    {
        var uri = Mqtt.Publish("t").Broker(C("main")).ContentType("application/json").Build();
        uri.Should().Contain("contentType=application%2Fjson");
    }

    [Fact]
    public void ResponseTopic_SetsParam()
    {
        var uri = Mqtt.Publish("t").Broker(C("main")).ResponseTopic(C("response/topic")).Build();
        uri.Should().Contain("responseTopic=response%2Ftopic");
    }

    // ── Implicit string conversion ──────────────────────────────────

    [Fact]
    public void ImplicitConversion_ReturnsUri()
    {
        string uri = Mqtt.Subscribe("test").Broker(C("main")).Qos(1);
        uri.Should().StartWith("mqtt:test?");
        uri.Should().Contain("mode=Subscribe");
        uri.Should().Contain("broker=main");
        uri.Should().Contain("qos=1");
    }

    [Fact]
    public void ToString_ReturnsSameAsBuild()
    {
        var builder = Mqtt.Publish("events").Broker(C("main")).Qos(2).Retain();
        builder.ToString().Should().Be(builder.Build());
    }

    // ── Full fluent chain ───────────────────────────────────────────

    [Fact]
    public void FullSubscribeChain_BuildsCorrectUri()
    {
        var uri = Mqtt.Subscribe("sensors/+/data")
            .Broker(C("main"))
            .Qos(1)
            .SharedSubscription(C("workers"))
            .ClientId(C("sub-1"))
            .Build();

        uri.Should().StartWith("mqtt:sensors/+/data?");
        uri.Should().Contain("mode=Subscribe");
        uri.Should().Contain("broker=main");
        uri.Should().Contain("qos=1");
        uri.Should().Contain("sharedSubscription=workers");
        uri.Should().Contain("clientId=sub-1");
    }

    [Fact]
    public void FullPublishChain_BuildsCorrectUri()
    {
        var uri = Mqtt.Publish("commands/device1")
            .Server(C("mqtt.example.com"))
            .Port(8883)
            .UseTls()
            .Username(C("admin"))
            .Password(C("pass"))
            .Qos(2)
            .Retain()
            .MessageExpiryInterval(600)
            .ContentType("application/json")
            .ResponseTopic(C("responses/device1"))
            .Build();

        uri.Should().StartWith("mqtt:commands/device1?");
        uri.Should().Contain("mode=Publish");
        uri.Should().Contain("server=mqtt.example.com");
        uri.Should().Contain("port=8883");
        uri.Should().Contain("useTls=true");
        uri.Should().Contain("username=admin");
        uri.Should().Contain("password=pass");
        uri.Should().Contain("qos=2");
        uri.Should().Contain("retain=true");
        uri.Should().Contain("messageExpiryInterval=600");
        uri.Should().Contain("contentType=application%2Fjson");
        uri.Should().Contain("responseTopic=responses%2Fdevice1");
    }

    // ── Defaults: optional params not included in URI ───────────────

    [Fact]
    public void Build_NoPort_OmitsPort()
    {
        var uri = Mqtt.Subscribe("t").Broker(C("main")).Build();
        uri.Should().NotContain("port=");
    }

    [Fact]
    public void Build_NoRetain_OmitsRetain()
    {
        var uri = Mqtt.Publish("t").Broker(C("main")).Build();
        uri.Should().NotContain("retain=");
    }

    [Fact]
    public void Build_NoTls_OmitsTls()
    {
        var uri = Mqtt.Subscribe("t").Broker(C("main")).Build();
        uri.Should().NotContain("useTls=");
    }

    [Fact]
    public void Build_NoCleanSession_OmitsCleanSession()
    {
        var uri = Mqtt.Subscribe("t").Broker(C("main")).Build();
        uri.Should().NotContain("cleanSession=");
    }
}
