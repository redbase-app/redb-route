using redb.Route.Abstractions;            // IExchange, IMessage
using redb.Route.As4;                     // As4Headers, As4Payload, exceptions

namespace ConformanceModule;

/// <summary>
/// The SUT Controller commands of the eDelivery conformance testing service (user guide v2.90, §5.3.1). A command is
/// an ordinary AS4 user message on the command channel; what it says travels in <c>eb:MessageProperties</c> and its
/// payloads.
/// <list type="bullet">
/// <item><b>Submit</b> (test driver to us): send this user message — its header values are the properties, its
/// payloads are the payloads of the command.</item>
/// <item><b>Deliver</b> (us to the test driver): we received this user message — the same properties and payloads,
/// read from what arrived.</item>
/// <item><b>Notify</b> (us to the test driver): this signal answered the message we sent — a receipt or an error.</item>
/// </list>
/// Each method rewrites the exchange into the message the next <c>To(...)</c> sends: the AS4 sender takes its header
/// values and every <c>redbAs4.property.*</c> header from the exchange, so what is left over from the previous message
/// is removed first.
/// </summary>
public static class Commands
{
    /// <summary>Header: name of the agreement the user message is sent under.</summary>
    public const string PartnerHeader = "as4ct.partner";

    /// <summary>Header: whether a missing receipt is redelivered (the ONEWAY_RETRY profile).</summary>
    public const string RetryHeader = "as4ct.retry";

    // The properties of a command that describe the user message itself; any other one is a property of that message.
    private static readonly HashSet<string> Described = new(StringComparer.OrdinalIgnoreCase)
    {
        "messageId", "conversationId", "refToMessageId",
        "fromPartyId", "fromPartyIdType", "fromPartyRole",
        "toPartyId", "toPartyIdType", "toPartyRole",
        "service", "serviceType", "action", "agreementRef", "mpc",
    };

    /// <summary>A Submit command becomes the user message it asks for.</summary>
    public static void SubmitToUserMessage(IExchange exchange, IReadOnlyDictionary<(string Service, string Action), ConformanceLeg> legs,
        Func<ConformanceLeg, string> partnerName)
    {
        var command = Read(exchange.In);
        var service = command.Value("service") ?? throw new InvalidOperationException("Submit command without a 'service' property.");
        var action = command.Value("action") ?? throw new InvalidOperationException("Submit command without an 'action' property.");
        if (!legs.TryGetValue((service, action), out var leg))
            throw new InvalidOperationException(
                $"Submit command for service '{service}', action '{action}': no such leg. Add it with the ExtraLegs setting of the context.");

        var payloads = Detach(exchange.In.Body);
        Clear(exchange.In);

        var h = exchange.In.Headers;
        if (command.Value("messageId") is { } messageId) h[As4Headers.MessageId] = messageId;
        if (command.Value("conversationId") is { } conversationId) h[As4Headers.ConversationId] = conversationId;
        if (command.Value("refToMessageId") is { } refToMessageId) h[As4Headers.RefToMessageId] = refToMessageId;
        foreach (var (name, value, type) in command.Properties.Where(p => !Described.Contains(p.Name)))
        {
            h[As4Headers.PropertyPrefix + name] = value;
            if (type is not null) h[As4Headers.PropertyTypePrefix + name] = type;
        }
        h[PartnerHeader] = partnerName(leg);
        h[RetryHeader] = leg.Retry;
        exchange.In.Body = payloads;
        exchange.In.ContentType = null;
    }

    /// <summary>A received user message becomes the Deliver command that reports it.</summary>
    public static void UserMessageToDeliver(IExchange exchange)
    {
        var message = exchange.In;
        string? Header(string name) => message.Headers.TryGetValue(name, out var value) ? value?.ToString() : null;

        var properties = new List<(string Name, string Value, string? Type)>();
        void Add(string name, string? value)
        {
            if (!string.IsNullOrEmpty(value)) properties.Add((name, value, null));
        }
        Add("messageId", Header(As4Headers.MessageId));
        Add("conversationId", Header(As4Headers.ConversationId));
        Add("refToMessageId", Header(As4Headers.RefToMessageId));
        Add("fromPartyId", Header(As4Headers.FromPartyId));
        Add("fromPartyRole", Header(As4Headers.FromRole));
        Add("toPartyId", Header(As4Headers.ToPartyId));
        Add("toPartyRole", Header(As4Headers.ToRole));
        Add("service", Header(As4Headers.Service));
        Add("serviceType", Header(As4Headers.ServiceType));
        Add("action", Header(As4Headers.Action));
        Add("agreementRef", Header(As4Headers.AgreementRef));
        // The message's own properties (originalSender, finalRecipient, trackingIdentifier, ...) travel on as they came.
        properties.AddRange(Read(message).Properties.Where(p => !Described.Contains(p.Name)));

        var payloads = Detach(message.Body);
        Clear(message);
        Write(message, properties);
        message.Body = payloads;
        message.ContentType = null;
    }

    /// <summary>The verified receipt of the user message we sent becomes a Notify command.</summary>
    public static void ReceiptToNotify(IExchange exchange)
    {
        var sent = exchange.In.Headers.TryGetValue(As4Headers.MessageId, out var id) ? id?.ToString() : null;
        Notify(exchange, sent, [("signalType", "receipt", null)]);
    }

    /// <summary>
    /// The failure of a send becomes a Notify command: the partner's ebMS error as it came, or EBMS:0301 MissingReceipt
    /// when no receipt arrived after the redeliveries (AS4_CP_TA15: reported to the producer).
    /// </summary>
    public static void FailureToNotify(IExchange exchange)
    {
        var sent = exchange.In.Headers.TryGetValue(As4Headers.MessageId, out var id) ? id?.ToString() : null;
        var failure = exchange.Exception
            ?? (exchange.Properties.TryGetValue("ExceptionCaught", out var caught) ? caught as Exception : null);

        var properties = new List<(string Name, string Value, string? Type)> { ("signalType", "error", null) };
        switch (failure)
        {
            case As4ErrorSignalException error:
                properties.Add(("errorCode", error.ErrorCode, null));
                if (!string.IsNullOrEmpty(error.ShortDescription)) properties.Add(("shortDescription", error.ShortDescription, null));
                if (!string.IsNullOrEmpty(error.Description)) properties.Add(("description", error.Description, null));
                break;
            case As4ReceiptException receipt:
                properties.Add(("errorCode", receipt.Code.Code, null));
                properties.Add(("shortDescription", receipt.Code.ShortDescription, null));
                properties.Add(("description", receipt.Message, null));
                break;
            case HttpRequestException:
                // The partner was not reached on any attempt: no receipt arrived, which is what reception awareness reports.
                properties.Add(("errorCode", As4ErrorCode.MissingReceipt.Code, null));
                properties.Add(("shortDescription", As4ErrorCode.MissingReceipt.ShortDescription, null));
                properties.Add(("description", failure.Message, null));
                break;
            default:
                properties.Add(("errorCode", "EBMS:0004", null));
                properties.Add(("shortDescription", "Other", null));
                properties.Add(("description", failure?.Message ?? "the message was not sent", null));
                break;
        }
        Notify(exchange, sent, properties);
    }

    private static void Notify(IExchange exchange, string? sentMessageId, List<(string Name, string Value, string? Type)> properties)
    {
        if (!string.IsNullOrEmpty(sentMessageId))
            properties.Add(("refToMessageId", sentMessageId, null));
        Clear(exchange.In);
        Write(exchange.In, properties);
        // A Notify has no payload: an empty list is a message without payloads.
        exchange.In.Body = Array.Empty<As4Payload>();
        exchange.In.ContentType = null;
    }

    // ── The message properties of the exchange ───────────────────────────────

    private sealed record CommandProperties(List<(string Name, string Value, string? Type)> Properties)
    {
        public string? Value(string name) =>
            Properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static CommandProperties Read(IMessage message)
    {
        var properties = new List<(string Name, string Value, string? Type)>();
        foreach (var (key, value) in message.Headers)
        {
            if (!key.StartsWith(As4Headers.PropertyPrefix, StringComparison.Ordinal) || value is null) continue;
            var name = key[As4Headers.PropertyPrefix.Length..];
            var type = message.Headers.TryGetValue(As4Headers.PropertyTypePrefix + name, out var t) ? t?.ToString() : null;
            properties.Add((name, value.ToString()!, type));
        }
        return new CommandProperties(properties);
    }

    private static void Write(IMessage message, IEnumerable<(string Name, string Value, string? Type)> properties)
    {
        foreach (var (name, value, type) in properties)
        {
            message.Headers[As4Headers.PropertyPrefix + name] = value;
            if (type is not null) message.Headers[As4Headers.PropertyTypePrefix + name] = type;
        }
    }

    /// <summary>Removes every AS4 header of the previous message, and ours.</summary>
    private static void Clear(IMessage message)
    {
        foreach (var key in message.Headers.Keys.Where(k => k.StartsWith(As4Headers.Prefix, StringComparison.Ordinal)
                                                            || k.StartsWith("as4ct.", StringComparison.Ordinal)).ToList())
            message.Headers.Remove(key);
    }

    /// <summary>
    /// Copies the payloads out of the receiving exchange, which closes their streams when it ends: the message that
    /// carries them on is sent from another exchange, after the receipt of this one went out.
    /// </summary>
    private static List<As4Payload> Detach(object? body)
    {
        var payloads = new List<As4Payload>();
        foreach (var payload in body as IEnumerable<As4Payload> ?? [])
        {
            var copy = new MemoryStream();
            if (payload.Content.CanSeek) payload.Content.Position = 0;
            payload.Content.CopyTo(copy);
            copy.Position = 0;
            payloads.Add(payload with { Content = copy });
        }
        return payloads;
    }
}
