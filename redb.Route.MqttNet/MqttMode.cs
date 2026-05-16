namespace redb.Route.MqttNet;

/// <summary>
/// MQTT endpoint mode: Subscribe (consumer) or Publish (producer).
/// </summary>
public enum MqttMode
{
    /// <summary>Subscribe to one or more topics — used with <c>From()</c>.</summary>
    Subscribe,

    /// <summary>Publish messages to a topic — used with <c>To()</c>.</summary>
    Publish
}
