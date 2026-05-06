using System.Text;
using Amqp;
using Amqp.Framing;
using AmqpMessage = global::Amqp.Message;

namespace redb.Route.Amqp;

/// <summary>
/// Helpers for AMQP message body extraction and property mapping.
/// </summary>
internal static class AmqpMessageHelper
{
    /// <summary>
    /// Extracts the body from an AMQP message as raw bytes or string.
    /// Handles: string, byte[], Data section, AmqpValue section.
    /// </summary>
    public static object ExtractBody(AmqpMessage message)
    {
        return message.Body switch
        {
            byte[] bytes => bytes,
            Data data => data.Binary,
            string s => Encoding.UTF8.GetBytes(s),
            AmqpValue value => Encoding.UTF8.GetBytes(value.Value?.ToString() ?? string.Empty),
            null => Array.Empty<byte>(),
            _ => Encoding.UTF8.GetBytes(message.Body.ToString() ?? string.Empty)
        };
    }

    /// <summary>
    /// Copies AMQP application-properties into a redb.Route message headers dictionary.
    /// </summary>
    public static void CopyApplicationProperties(AmqpMessage amqpMessage, redb.Route.Core.Message routeMessage)
    {
        if (amqpMessage.ApplicationProperties?.Map == null) return;

        foreach (var kvp in amqpMessage.ApplicationProperties.Map)
        {
            var key = kvp.Key?.ToString();
            if (string.IsNullOrEmpty(key)) continue;

            object? value = kvp.Value switch
            {
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                string str => str,
                _ => kvp.Value
            };

            if (value is not null)
                routeMessage.Headers[key] = value;
        }
    }
}
