using IBM.XMS;
using RouteMessage = redb.Route.Core.Message;
using XmsMessage = IBM.XMS.IMessage;

namespace redb.Route.IbmMq;

/// <summary>
/// Maps an XMS message's JMS/MQMD metadata and user headers onto a redb route message, so the
/// event-driven (XMS) receive path carries the same <c>redbIbmMq.*</c> and application headers as the
/// poll path (<see cref="IbmMqMessageHelper.CopyMqmdToHeaders"/> +
/// <see cref="IbmMqMessageHelper.CopyRfh2UserProperties"/>).
/// <para>
/// User headers are read from the single JSON-encoded <see cref="IbmMqHeaders.HeaderKeys"/> property the
/// redb producer writes — the exact counterpart of the poll consumer's RFH2 read — so a redb→redb hop
/// round-trips application headers identically on either receive mode.
/// </para>
/// </summary>
internal static class IbmMqXmsHeaderMapper
{
    public static void CopyToHeaders(XmsMessage msg, RouteMessage routeMsg, string destination, IbmMqEndpointOptions options)
    {
        // MQMD-equivalent metadata — gated by MqmdReadEnabled exactly like the poll path.
        if (options.MqmdReadEnabled)
        {
            routeMsg.Headers[IbmMqHeaders.Destination] = destination;
            routeMsg.Headers[IbmMqHeaders.QueueManager] = options.QueueManager;

            var msgId = MessageIdToHex(TryGet(() => msg.JMSMessageID));
            if (!string.IsNullOrEmpty(msgId))
                routeMsg.Headers[IbmMqHeaders.MsgId] = msgId;

            var correlBytes = TryGet(() => msg.JMSCorrelationIDAsBytes);
            if (correlBytes is { Length: > 0 })
                routeMsg.Headers[IbmMqHeaders.CorrelId] = IbmMqMessageHelper.BytesToHex(correlBytes);

            var priority = TryGetStruct(() => msg.JMSPriority);
            if (priority.HasValue)
                routeMsg.Headers[IbmMqHeaders.Priority] = priority.Value;

            var replyToName = TryGet(() => msg.JMSReplyTo?.Name);
            if (!string.IsNullOrEmpty(replyToName))
                routeMsg.Headers[IbmMqHeaders.ReplyToQueue] = BareQueueName(replyToName);

            var deliveryCount = TryGetStruct(() => msg.GetIntProperty(XMSC.JMSX_DELIVERY_COUNT));
            if (deliveryCount is > 0)
                routeMsg.Headers[IbmMqHeaders.BackoutCount] = deliveryCount.Value - 1;
        }

        CopyUserHeaders(msg, routeMsg);
    }

    /// <summary>Reads the redb producer's JSON header catalogue and applies each user header.</summary>
    private static void CopyUserHeaders(XmsMessage msg, RouteMessage routeMsg)
    {
        var json = TryGet(() => msg.GetStringProperty(IbmMqHeaders.HeaderCatalogue));

        if (string.IsNullOrEmpty(json)) return;

        try
        {
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (dict is null) return;

            foreach (var (key, value) in dict)
                routeMsg.Headers[key] = value;
        }
        catch { /* malformed JSON — skip, matching the poll path */ }
    }

    /// <summary>JMS message id ("ID:" + hex) → the raw hex the poll path stores for MQMD MsgId.</summary>
    private static string? MessageIdToHex(string? jmsMessageId)
    {
        if (string.IsNullOrEmpty(jmsMessageId)) return null;
        return jmsMessageId.StartsWith("ID:", StringComparison.Ordinal) ? jmsMessageId[3..] : jmsMessageId;
    }

    /// <summary>Bare MQ queue name from an XMS destination name (<c>queue://qmgr/NAME</c> or plain).</summary>
    private static string BareQueueName(string name)
    {
        var slash = name.LastIndexOf('/');
        return (slash >= 0 ? name[(slash + 1)..] : name).Trim();
    }

    private static T? TryGet<T>(Func<T?> f) where T : class
    {
        try { return f(); } catch { return null; }
    }

    private static T? TryGetStruct<T>(Func<T> f) where T : struct
    {
        try { return f(); } catch { return null; }
    }
}
