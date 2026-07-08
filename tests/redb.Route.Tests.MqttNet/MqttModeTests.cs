using redb.Route.MqttNet;

namespace redb.Route.Tests.MqttNet;

public class MqttModeTests
{
    [Fact]
    public void Subscribe_HasValue0()
    {
        ((int)MqttMode.Subscribe).Should().Be(0);
    }

    [Fact]
    public void Publish_HasValue1()
    {
        ((int)MqttMode.Publish).Should().Be(1);
    }

    [Fact]
    public void Enum_HasTwoValues()
    {
        Enum.GetValues<MqttMode>().Should().HaveCount(2);
    }
}
