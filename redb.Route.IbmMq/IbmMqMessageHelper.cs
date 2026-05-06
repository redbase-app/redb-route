using System.Text;
using IBM.WMQ;
using redb.Route.Abstractions;
using RouteMessage = redb.Route.Core.Message;

namespace redb.Route.IbmMq;

/// <summary>
/// Helpers for IBM MQ message body extraction, MQMD→headers mapping, and exchange→MQMessage building.
/// </summary>
public static class IbmMqMessageHelper
{
    /// <summary>
    /// Extracts the body from an <see cref="MQMessage"/> as a string or byte[].
    /// Uses Format and CCSID to decide encoding.
    /// </summary>
    public static object ExtractBody(MQMessage msg)
    {
        var dataLength = msg.DataLength;
        if (dataLength == 0) return string.Empty;

        // MQFMT_STRING or MQFMT_RF_HEADER_2 → read as text
        var fmt = msg.Format.Trim();
        if (fmt == MQC.MQFMT_STRING.Trim() || fmt == MQC.MQFMT_RF_HEADER_2.Trim())
        {
            return msg.ReadString(dataLength);
        }

        // Binary payload
        return msg.ReadBytes(dataLength);
    }

    /// <summary>
    /// Copies MQMD fields into redb.Route message headers with <c>redbIbmMq.*</c> prefix.
    /// </summary>
    public static void CopyMqmdToHeaders(MQMessage mqMsg, RouteMessage routeMsg, string destination, string queueManager)
    {
        routeMsg.Headers[IbmMqHeaders.Destination] = destination;
        routeMsg.Headers[IbmMqHeaders.QueueManager] = queueManager;

        if (mqMsg.MessageId != null && mqMsg.MessageId.Length > 0)
            routeMsg.Headers[IbmMqHeaders.MsgId] = BytesToHex(mqMsg.MessageId);

        if (mqMsg.CorrelationId != null && mqMsg.CorrelationId.Length > 0)
            routeMsg.Headers[IbmMqHeaders.CorrelId] = BytesToHex(mqMsg.CorrelationId);

        routeMsg.Headers[IbmMqHeaders.Format] = mqMsg.Format.Trim();
        routeMsg.Headers[IbmMqHeaders.CCSID] = mqMsg.CharacterSet;
        routeMsg.Headers[IbmMqHeaders.Encoding] = mqMsg.Encoding;
        routeMsg.Headers[IbmMqHeaders.Persistence] = mqMsg.Persistence;
        routeMsg.Headers[IbmMqHeaders.Priority] = mqMsg.Priority;
        routeMsg.Headers[IbmMqHeaders.Expiry] = mqMsg.Expiry;
        routeMsg.Headers[IbmMqHeaders.MsgType] = mqMsg.MessageType;
        routeMsg.Headers[IbmMqHeaders.Feedback] = mqMsg.Feedback;
        routeMsg.Headers[IbmMqHeaders.ReportOptions] = mqMsg.Report;
        routeMsg.Headers[IbmMqHeaders.BackoutCount] = mqMsg.BackoutCount;

        if (!string.IsNullOrWhiteSpace(mqMsg.ReplyToQueueName))
            routeMsg.Headers[IbmMqHeaders.ReplyToQueue] = mqMsg.ReplyToQueueName.Trim();

        if (!string.IsNullOrWhiteSpace(mqMsg.ReplyToQueueManagerName))
            routeMsg.Headers[IbmMqHeaders.ReplyToQueueManager] = mqMsg.ReplyToQueueManagerName.Trim();

        if (!string.IsNullOrWhiteSpace(mqMsg.UserId))
            routeMsg.Headers[IbmMqHeaders.UserIdentifier] = mqMsg.UserId.Trim();

        if (!string.IsNullOrWhiteSpace(mqMsg.PutApplicationName))
            routeMsg.Headers[IbmMqHeaders.PutApplName] = mqMsg.PutApplicationName.Trim();

        routeMsg.Headers[IbmMqHeaders.PutApplType] = mqMsg.PutApplicationType;

        if (mqMsg.GroupId != null && mqMsg.GroupId.Length > 0 && !IsEmptyBytes(mqMsg.GroupId))
            routeMsg.Headers[IbmMqHeaders.GroupId] = BytesToHex(mqMsg.GroupId);

        if (mqMsg.MessageSequenceNumber > 0)
            routeMsg.Headers[IbmMqHeaders.MsgSeqNumber] = mqMsg.MessageSequenceNumber;

        try
        {
            var putDate = mqMsg.PutDateTime;
            routeMsg.Headers[IbmMqHeaders.PutDateTime] = putDate;
        }
        catch
        {
            // PutDateTime may throw if date fields are empty/invalid
        }
    }

    /// <summary>
    /// Copies RFH2 user properties (if present) into exchange headers.
    /// IBM MQ JMS messages carry user-defined properties in the MQRFH2 usr folder.
    /// </summary>
    public static void CopyRfh2UserProperties(MQMessage mqMsg, RouteMessage routeMsg)
    {
        string? json = null;
        try { json = mqMsg.GetStringProperty(IbmMqHeaders.HeaderKeys); }
        catch { /* property not present */ }

        if (string.IsNullOrEmpty(json)) return;

        try
        {
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (dict is null) return;

            foreach (var (key, value) in dict)
                routeMsg.Headers[key] = value;
        }
        catch { /* malformed JSON — skip */ }
    }

    /// <summary>
    /// Builds an <see cref="MQMessage"/> from an exchange for sending.
    /// </summary>
    public static MQMessage BuildOutgoingMessage(
        IExchange exchange,
        IbmMqEndpointOptions options)
    {
        var msg = new MQMessage
        {
            CharacterSet = options.CCSID,
            Format = MQC.MQFMT_STRING,
        };

        // Persistence: header > expression > static
        var persistence = options.Persistence;
        if (options.PersistenceExpression is not null)
        {
            var resolved = options.ResolveOption(options.PersistenceExpression, exchange);
            if (resolved is not null && Enum.TryParse<IbmMqPersistence>(resolved, true, out var parsedPers))
                persistence = parsedPers;
        }

        msg.Persistence = persistence switch
        {
            IbmMqPersistence.Persistent => MQC.MQPER_PERSISTENT,
            IbmMqPersistence.NonPersistent => MQC.MQPER_NOT_PERSISTENT,
            _ => MQC.MQPER_PERSISTENCE_AS_Q_DEF
        };

        // Priority: header > expression > static
        var priority = options.Priority;
        if (options.PriorityExpression is not null)
        {
            var resolved = options.ResolveOption(options.PriorityExpression, exchange);
            if (resolved is not null && int.TryParse(resolved, out var parsedPrio))
                priority = parsedPrio;
        }

        if (priority >= 0)
            msg.Priority = priority;

        // Expiry: header > expression > static
        var expiry = options.Expiry;
        if (options.ExpiryExpression is not null)
        {
            var resolved = options.ResolveOption(options.ExpiryExpression, exchange);
            if (resolved is not null && int.TryParse(resolved, out var parsedExp))
                expiry = parsedExp;
        }

        if (expiry >= 0)
            msg.Expiry = expiry;

        // Message type: header > expression > static
        var messageType = (int)options.MessageType;
        if (options.MessageTypeExpression is not null)
        {
            var resolved = options.ResolveOption(options.MessageTypeExpression, exchange);
            if (resolved is not null && Enum.TryParse<IbmMqMessageType>(resolved, true, out var parsedMt))
                messageType = (int)parsedMt;
        }

        msg.MessageType = messageType;

        // Body
        var body = exchange.In.Body;
        var contentType = exchange.In.ContentType;
        switch (body)
        {
            case byte[] bytes:
                msg.Write(bytes);
                msg.Format = contentType is not null ? ContentTypeToFormat(contentType) : MQC.MQFMT_NONE;
                break;
            case string str:
                msg.WriteString(str);
                msg.Format = contentType is not null ? ContentTypeToFormat(contentType) : MQC.MQFMT_STRING;
                break;
            case null:
                break;
            default:
                msg.WriteString(body.ToString() ?? string.Empty);
                break;
        }

        // Copy exchange headers → single JSON-encoded MQ property
        // (MQ property names forbid hyphens, so individual SetStringProperty won't
        //  work for standard HTTP-style headers like X-Custom-Id)
        var customHeaders = new Dictionary<string, string>();
        foreach (var (key, value) in exchange.In.Headers)
        {
            if (IbmMqHeaders.IsRedbHeader(key)) continue;
            if (value is null) continue;
            customHeaders[key] = value.ToString()!;
        }

        if (customHeaders.Count > 0)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(customHeaders);
            msg.SetStringProperty(IbmMqHeaders.HeaderKeys, json);
        }

        // Allow direct MQMD writes from headers (if enabled)
        if (options.MqmdWriteEnabled)
            ApplyMqmdFromHeaders(msg, exchange);

        return msg;
    }

    /// <summary>
    /// Overwrites MQMD fields from exchange headers when MqmdWriteEnabled is true.
    /// </summary>
    private static void ApplyMqmdFromHeaders(MQMessage msg, IExchange exchange)
    {
        var headers = exchange.In.Headers;

        if (headers.TryGetValue(IbmMqHeaders.CorrelId, out var cid) && cid is string cidStr)
            msg.CorrelationId = HexToBytes(cidStr);

        if (headers.TryGetValue(IbmMqHeaders.Priority, out var prio) && prio is int prioInt)
            msg.Priority = prioInt;

        if (headers.TryGetValue(IbmMqHeaders.Expiry, out var exp) && exp is int expInt)
            msg.Expiry = expInt;

        if (headers.TryGetValue(IbmMqHeaders.Persistence, out var pers) && pers is int persInt)
            msg.Persistence = persInt;

        if (headers.TryGetValue(IbmMqHeaders.ReplyToQueue, out var rtq) && rtq is string rtqStr)
            msg.ReplyToQueueName = rtqStr;

        if (headers.TryGetValue(IbmMqHeaders.ReplyToQueueManager, out var rtqm) && rtqm is string rtqmStr)
            msg.ReplyToQueueManagerName = rtqmStr;

        if (headers.TryGetValue(IbmMqHeaders.MsgType, out var mt) && mt is int mtInt)
            msg.MessageType = mtInt;

        if (headers.TryGetValue(IbmMqHeaders.Format, out var fmt) && fmt is string fmtStr)
            msg.Format = fmtStr;

        if (headers.TryGetValue(IbmMqHeaders.GroupId, out var gid) && gid is string gidStr)
            msg.GroupId = HexToBytes(gidStr);

        if (headers.TryGetValue(IbmMqHeaders.MsgSeqNumber, out var seq) && seq is int seqInt)
            msg.MessageSequenceNumber = seqInt;
    }

    // ── Format ↔ ContentType mapping ──

    /// <summary>Maps MQMD Format to a MIME content type.</summary>
    public static string? FormatToContentType(string? format)
    {
        if (format is null) return null;
        var fmt = format.Trim();
        if (fmt.Length == 0) return "application/octet-stream"; // MQFMT_NONE = 8 spaces
        if (fmt == MQC.MQFMT_STRING.Trim()) return "text/plain";
        if (fmt == MQC.MQFMT_RF_HEADER_2.Trim()) return "text/plain";
        return null;
    }

    /// <summary>Maps a MIME content type to the best MQMD Format.</summary>
    public static string ContentTypeToFormat(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return MQC.MQFMT_STRING;
        if (contentType.StartsWith("application/octet-stream", StringComparison.OrdinalIgnoreCase))
            return MQC.MQFMT_NONE;
        // text/*, application/json, application/xml etc. → string format
        return MQC.MQFMT_STRING;
    }

    // ── Helpers ──

    internal static string BytesToHex(byte[] bytes)
    {
        return Convert.ToHexString(bytes);
    }

    internal static byte[] HexToBytes(string hex)
    {
        return Convert.FromHexString(hex);
    }

    private static bool IsEmptyBytes(byte[] bytes)
    {
        foreach (var b in bytes)
            if (b != 0) return false;
        return true;
    }
}
