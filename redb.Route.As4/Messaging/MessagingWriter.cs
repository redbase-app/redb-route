using System.Globalization;
using System.Xml;

namespace redb.Route.As4.Messaging;

/// <summary>
/// Writes the typed model into a SOAP 1.2 envelope: <c>eb:Messaging</c> (with <c>mustUnderstand="true"</c>) as the
/// only header block and an empty body — AS4 carries payloads as attachments. The namespaces are declared on the
/// envelope explicitly, so the document canonicalizes the same before and after serialization (see
/// <c>ExclusiveCanonicalizer.DeclareNamespaces</c>).
/// </summary>
internal static class MessagingWriter
{
    private const string SoapPrefix = "soapenv";
    private const string EbPrefix = "eb";

    /// <summary>A new envelope carrying <paramref name="message"/>.</summary>
    public static XmlDocument CreateEnvelope(UserMessage message) =>
        CreateEnvelope(messaging => WriteUserMessage(messaging, message));

    /// <summary>A new envelope carrying <paramref name="signal"/>.</summary>
    public static XmlDocument CreateEnvelope(SignalMessage signal) =>
        CreateEnvelope(messaging => WriteSignalMessage(messaging, signal));

    private static XmlDocument CreateEnvelope(Action<XmlElement> fill)
    {
        var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        var envelope = doc.CreateElement(SoapPrefix, "Envelope", EbmsNamespaces.Soap12);
        envelope.SetAttribute("xmlns:" + SoapPrefix, EbmsNamespaces.Soap12);
        envelope.SetAttribute("xmlns:" + EbPrefix, EbmsNamespaces.Eb);
        doc.AppendChild(envelope);

        var header = Add(envelope, SoapPrefix, "Header", EbmsNamespaces.Soap12);
        var messaging = Add(header, EbPrefix, "Messaging", EbmsNamespaces.Eb);
        var mustUnderstand = doc.CreateAttribute(SoapPrefix, "mustUnderstand", EbmsNamespaces.Soap12);
        mustUnderstand.Value = "true";
        messaging.Attributes.Append(mustUnderstand);
        fill(messaging);

        Add(envelope, SoapPrefix, "Body", EbmsNamespaces.Soap12);
        return doc;
    }

    private static void WriteUserMessage(XmlElement messaging, UserMessage message)
    {
        var user = Add(messaging, EbPrefix, "UserMessage", EbmsNamespaces.Eb);
        if (message.Mpc is not null)
            user.SetAttribute("mpc", message.Mpc);

        WriteMessageInfo(user, message.MessageInfo);

        var partyInfo = Eb(user, "PartyInfo");
        WriteParty(Eb(partyInfo, "From"), message.From);
        WriteParty(Eb(partyInfo, "To"), message.To);

        var collaboration = Eb(user, "CollaborationInfo");
        if (message.CollaborationInfo.AgreementRef is { } agreement)
        {
            var element = Eb(collaboration, "AgreementRef", agreement.Value);
            if (agreement.Type is not null) element.SetAttribute("type", agreement.Type);
            if (agreement.PMode is not null) element.SetAttribute("pmode", agreement.PMode);
        }
        var service = Eb(collaboration, "Service", message.CollaborationInfo.Service.Value);
        if (message.CollaborationInfo.Service.Type is not null) service.SetAttribute("type", message.CollaborationInfo.Service.Type);
        Eb(collaboration, "Action", message.CollaborationInfo.Action);
        Eb(collaboration, "ConversationId", message.CollaborationInfo.ConversationId);

        if (message.MessageProperties.Count > 0)
            WriteProperties(Eb(user, "MessageProperties"), message.MessageProperties);

        if (message.PayloadInfo.Count > 0)
        {
            var payloadInfo = Eb(user, "PayloadInfo");
            foreach (var part in message.PayloadInfo)
            {
                var partInfo = Eb(payloadInfo, "PartInfo");
                if (part.Href is not null) partInfo.SetAttribute("href", part.Href);
                if (part.Properties.Count > 0)
                    WriteProperties(Eb(partInfo, "PartProperties"), part.Properties);
            }
        }
    }

    private static void WriteSignalMessage(XmlElement messaging, SignalMessage signal)
    {
        var element = Add(messaging, EbPrefix, "SignalMessage", EbmsNamespaces.Eb);
        WriteMessageInfo(element, signal.MessageInfo);

        if (signal.ReceiptContent is { } content)
        {
            var receipt = Eb(element, "Receipt");
            foreach (var child in content)
                receipt.AppendChild(receipt.OwnerDocument.ImportNode(child, deep: true));
        }

        foreach (var error in signal.Errors)
        {
            var e = Eb(element, "Error");
            e.SetAttribute("errorCode", error.ErrorCode);
            e.SetAttribute("severity", error.Severity == As4ErrorSeverity.Failure ? "failure" : "warning");
            if (error.Category is not null) e.SetAttribute("category", error.Category);
            if (error.Origin is not null) e.SetAttribute("origin", error.Origin);
            if (error.RefToMessageInError is not null) e.SetAttribute("refToMessageInError", error.RefToMessageInError);
            if (error.ShortDescription is not null) e.SetAttribute("shortDescription", error.ShortDescription);
            if (error.Description is not null)
            {
                var description = Eb(e, "Description", error.Description);
                var lang = description.OwnerDocument.CreateAttribute("xml", "lang", "http://www.w3.org/XML/1998/namespace");
                lang.Value = "en";
                description.Attributes.Append(lang);
            }
            if (error.ErrorDetail is not null) Eb(e, "ErrorDetail", error.ErrorDetail);
        }

        if (signal.PullRequestMpc is not null)
            Eb(element, "PullRequest").SetAttribute("mpc", signal.PullRequestMpc);
    }

    private static void WriteMessageInfo(XmlElement parent, MessageInfo info)
    {
        var element = Eb(parent, "MessageInfo");
        Eb(element, "Timestamp", info.Timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        Eb(element, "MessageId", info.MessageId);
        if (info.RefToMessageId is not null)
            Eb(element, "RefToMessageId", info.RefToMessageId);
    }

    private static void WriteParty(XmlElement element, Party party)
    {
        foreach (var id in party.PartyIds)
        {
            var partyId = Eb(element, "PartyId", id.Value);
            if (id.Type is not null) partyId.SetAttribute("type", id.Type);
        }
        Eb(element, "Role", party.Role);
    }

    private static void WriteProperties(XmlElement element, IReadOnlyList<Property> properties)
    {
        foreach (var property in properties)
        {
            var p = Eb(element, "Property", property.Value);
            p.SetAttribute("name", property.Name);
            if (property.Type is not null) p.SetAttribute("type", property.Type);
        }
    }

    private static XmlElement Eb(XmlElement parent, string localName, string? text = null)
    {
        var element = Add(parent, EbPrefix, localName, EbmsNamespaces.Eb);
        if (text is not null) element.InnerText = text;
        return element;
    }

    private static XmlElement Add(XmlElement parent, string prefix, string localName, string ns)
    {
        var element = parent.OwnerDocument.CreateElement(prefix, localName, ns);
        parent.AppendChild(element);
        return element;
    }
}

/// <summary>Message ids as ebMS requires them: globally unique, RFC 2822 <c>id-left@id-right</c> without brackets.</summary>
internal static class MessageIdFactory
{
    /// <summary>A new id under <paramref name="host"/> (the node's public name, never the machine name).</summary>
    public static string New(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return Guid.NewGuid().ToString("D") + "@" + host;
    }
}
