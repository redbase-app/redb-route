using redb.Route.MqttNet;

namespace redb.Route.Tests.MqttNet;

public class MqttHeadersTests
{
    [Fact]
    public void Prefix_IsRedbMqttDot()
    {
        MqttHeaders.Prefix.Should().Be("redbMqtt.");
    }

    [Theory]
    [InlineData(nameof(MqttHeaders.Topic), "redbMqtt.topic")]
    [InlineData(nameof(MqttHeaders.Qos), "redbMqtt.qos")]
    [InlineData(nameof(MqttHeaders.Retain), "redbMqtt.retain")]
    [InlineData(nameof(MqttHeaders.ContentType), "redbMqtt.contentType")]
    [InlineData(nameof(MqttHeaders.ResponseTopic), "redbMqtt.responseTopic")]
    [InlineData(nameof(MqttHeaders.CorrelationData), "redbMqtt.correlationData")]
    [InlineData(nameof(MqttHeaders.MessageExpiryInterval), "redbMqtt.messageExpiryInterval")]
    [InlineData(nameof(MqttHeaders.Broker), "redbMqtt.broker")]
    [InlineData(nameof(MqttHeaders.ClientId), "redbMqtt.clientId")]
    [InlineData(nameof(MqttHeaders.UserProperties), "redbMqtt.userProperties")]
    public void HeaderConstants_HaveCorrectValues(string fieldName, string expected)
    {
        var field = typeof(MqttHeaders).GetField(fieldName);
        field.Should().NotBeNull();
        field!.GetValue(null).Should().Be(expected);
    }

    [Fact]
    public void AllHeaders_StartWithPrefix()
    {
        var fields = typeof(MqttHeaders).GetFields(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        foreach (var field in fields)
        {
            if (field.Name == nameof(MqttHeaders.Prefix)) continue;
            var value = (string)field.GetValue(null)!;
            value.Should().StartWith(MqttHeaders.Prefix,
                $"header {field.Name} should start with prefix '{MqttHeaders.Prefix}'");
        }
    }
}
