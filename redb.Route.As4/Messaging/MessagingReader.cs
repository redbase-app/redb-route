using System.Globalization;
using System.Xml;

namespace redb.Route.As4.Messaging;

/// <summary>
/// Reads <c>eb:Messaging</c> from a SOAP 1.2 envelope into the typed model, enforcing the structure of ebMS 3.0
/// Core §5.2 element by element: a missing required element, a repeated singleton, an element the header does
/// not define or an unparsable value is <see cref="As4ErrorCode.InvalidHeader"/> with the path of the offence.
/// <para>
/// This is the check the OASIS schema would make; the schema itself is not used because it imports the SOAP
/// schemas by network location, and nothing here resolves anything over the network (<c>SafeXml</c>, no
/// resolver). Elements are matched by namespace and local name, never by prefix.
/// </para>
/// </summary>
internal static class MessagingReader
{
    /// <summary>
    /// The <c>eb:Messaging</c> of <paramref name="envelope"/>. The envelope must be SOAP 1.2 and carry exactly one
    /// <c>eb:Messaging</c> as a direct child of the header.
    /// </summary>
    public static EbmsMessaging Read(XmlDocument envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var root = envelope.DocumentElement;
        if (root is null || root.LocalName != "Envelope" || root.NamespaceURI != EbmsNamespaces.Soap12)
            throw Invalid("/", "the document is not a SOAP 1.2 envelope");
        var header = One(root, "Header", EbmsNamespaces.Soap12, "/Envelope");
        var messaging = One(header, "Messaging", EbmsNamespaces.Eb, "/Envelope/Header");

        var users = new List<UserMessage>();
        var signals = new List<SignalMessage>();
        foreach (var child in Elements(messaging))
        {
            if (child.NamespaceURI == EbmsNamespaces.Eb && child.LocalName == "UserMessage")
                users.Add(ReadUserMessage(child, "/Messaging/UserMessage"));
            else if (child.NamespaceURI == EbmsNamespaces.Eb && child.LocalName == "SignalMessage")
                signals.Add(ReadSignalMessage(child, "/Messaging/SignalMessage"));
            else
                throw Invalid("/Messaging", $"unexpected element {{{child.NamespaceURI}}}{child.LocalName}");
        }

        if (users.Count == 0 && signals.Count == 0)
            throw Invalid("/Messaging", "carries neither a user message nor a signal");
        if (users.Count > 1)
            throw Invalid("/Messaging", "carries more than one user message (AS4 profile §2.1)");

        return new EbmsMessaging(users, signals);
    }

    private static UserMessage ReadUserMessage(XmlElement element, string path)
    {
        Only(element, path, "MessageInfo", "PartyInfo", "CollaborationInfo", "MessageProperties", "PayloadInfo");

        var info = ReadMessageInfo(One(element, "MessageInfo", EbmsNamespaces.Eb, path), path + "/MessageInfo");
        var partyInfoPath = path + "/PartyInfo";
        var partyInfo = One(element, "PartyInfo", EbmsNamespaces.Eb, path);
        Only(partyInfo, partyInfoPath, "From", "To");
        var from = ReadParty(One(partyInfo, "From", EbmsNamespaces.Eb, partyInfoPath), partyInfoPath + "/From");
        var to = ReadParty(One(partyInfo, "To", EbmsNamespaces.Eb, partyInfoPath), partyInfoPath + "/To");
        var collaboration = ReadCollaboration(One(element, "CollaborationInfo", EbmsNamespaces.Eb, path), path + "/CollaborationInfo");

        var properties = Optional(element, "MessageProperties", path) is { } props
            ? ReadProperties(props, path + "/MessageProperties")
            : [];

        var parts = new List<PartInfo>();
        if (Optional(element, "PayloadInfo", path) is { } payloadInfo)
        {
            var payloadPath = path + "/PayloadInfo";
            Only(payloadInfo, payloadPath, "PartInfo");
            foreach (var partInfo in Many(payloadInfo, "PartInfo", payloadPath, atLeastOne: true))
                parts.Add(ReadPartInfo(partInfo, payloadPath + "/PartInfo"));
        }

        var mpc = element.GetAttributeNode("mpc")?.Value;
        return new UserMessage(info, from, to, collaboration, properties, parts, mpc);
    }

    private static SignalMessage ReadSignalMessage(XmlElement element, string path)
    {
        Only(element, path, "MessageInfo", "PullRequest", "Receipt", "Error");
        var info = ReadMessageInfo(One(element, "MessageInfo", EbmsNamespaces.Eb, path), path + "/MessageInfo");

        var pull = Optional(element, "PullRequest", path);
        var receipt = Optional(element, "Receipt", path);
        var errors = Many(element, "Error", path, atLeastOne: false).Select(e => ReadError(e, path + "/Error")).ToList();

        var kinds = (pull is null ? 0 : 1) + (receipt is null ? 0 : 1) + (errors.Count == 0 ? 0 : 1);
        if (kinds != 1)
            throw Invalid(path, "must carry exactly one of PullRequest, Receipt or Error elements");

        IReadOnlyList<XmlElement>? receiptContent = null;
        if (receipt is not null)
        {
            receiptContent = Elements(receipt).Select(e => (XmlElement)e.CloneNode(deep: true)).ToList();
            if (receiptContent.Count == 0)
                throw Invalid(path + "/Receipt", "is empty");
        }

        string? pullMpc = null;
        if (pull is not null)
            pullMpc = pull.GetAttributeNode("mpc")?.Value ?? EbmsNamespaces.DefaultMpc;

        if (receipt is not null && info.RefToMessageId is null)
            throw Invalid(path + "/MessageInfo", "a receipt must carry RefToMessageId");

        return new SignalMessage(info, receiptContent, errors, pullMpc);
    }

    private static MessageInfo ReadMessageInfo(XmlElement element, string path)
    {
        Only(element, path, "Timestamp", "MessageId", "RefToMessageId");
        var timestampText = Text(One(element, "Timestamp", EbmsNamespaces.Eb, path), path + "/Timestamp");
        if (!DateTimeOffset.TryParse(timestampText, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
            throw Invalid(path + "/Timestamp", $"'{timestampText}' is not an xsd:dateTime");

        var messageId = Text(One(element, "MessageId", EbmsNamespaces.Eb, path), path + "/MessageId");
        var refTo = Optional(element, "RefToMessageId", path) is { } r ? Text(r, path + "/RefToMessageId") : null;
        return new MessageInfo(timestamp, messageId, refTo);
    }

    private static Party ReadParty(XmlElement element, string path)
    {
        Only(element, path, "PartyId", "Role");
        var ids = Many(element, "PartyId", path, atLeastOne: true)
            .Select(p => new PartyId(Text(p, path + "/PartyId"), AttributeOrNull(p, "type")))
            .ToList();
        var role = Text(One(element, "Role", EbmsNamespaces.Eb, path), path + "/Role");
        return new Party(ids, role);
    }

    private static CollaborationInfo ReadCollaboration(XmlElement element, string path)
    {
        Only(element, path, "AgreementRef", "Service", "Action", "ConversationId");

        AgreementRef? agreement = null;
        if (Optional(element, "AgreementRef", path) is { } a)
            agreement = new AgreementRef(Text(a, path + "/AgreementRef"), AttributeOrNull(a, "type"), AttributeOrNull(a, "pmode"));

        var serviceElement = One(element, "Service", EbmsNamespaces.Eb, path);
        var service = new Service(Text(serviceElement, path + "/Service"), AttributeOrNull(serviceElement, "type"));
        var action = Text(One(element, "Action", EbmsNamespaces.Eb, path), path + "/Action");
        var conversation = Text(One(element, "ConversationId", EbmsNamespaces.Eb, path), path + "/ConversationId");
        return new CollaborationInfo(agreement, service, action, conversation);
    }

    private static PartInfo ReadPartInfo(XmlElement element, string path)
    {
        Only(element, path, "Schema", "Description", "PartProperties");
        var href = AttributeOrNull(element, "href");
        var properties = Optional(element, "PartProperties", path) is { } p
            ? ReadProperties(p, path + "/PartProperties")
            : [];
        return new PartInfo(href, properties);
    }

    private static IReadOnlyList<Property> ReadProperties(XmlElement element, string path)
    {
        Only(element, path, "Property");
        var properties = Many(element, "Property", path, atLeastOne: true)
            .Select(p => new Property(
                AttributeOrNull(p, "name") ?? throw Invalid(path + "/Property", "has no name"),
                p.InnerText,
                AttributeOrNull(p, "type")))
            .ToList();

        var repeated = properties.GroupBy(p => p.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (repeated is not null)
            throw Invalid(path, $"property '{repeated.Key}' appears more than once");
        return properties;
    }

    private static EbmsError ReadError(XmlElement element, string path)
    {
        Only(element, path, "Description", "ErrorDetail");
        var code = AttributeOrNull(element, "errorCode") ?? throw Invalid(path, "has no errorCode");
        var severityText = AttributeOrNull(element, "severity") ?? throw Invalid(path, "has no severity");
        var severity = severityText switch
        {
            "failure" => As4ErrorSeverity.Failure,
            "warning" => As4ErrorSeverity.Warning,
            _ => throw Invalid(path, $"severity '{severityText}' is neither failure nor warning"),
        };
        return new EbmsError(code, severity,
            AttributeOrNull(element, "category"),
            AttributeOrNull(element, "origin"),
            AttributeOrNull(element, "refToMessageInError"),
            AttributeOrNull(element, "shortDescription"),
            Optional(element, "Description", path)?.InnerText,
            Optional(element, "ErrorDetail", path)?.InnerText);
    }

    // ── Structure helpers ────────────────────────────────────────────────────

    private static IEnumerable<XmlElement> Elements(XmlElement parent) => parent.ChildNodes.OfType<XmlElement>();

    /// <summary>Refuses any child element that is not one of <paramref name="allowed"/> in the ebMS namespace.</summary>
    private static void Only(XmlElement element, string path, params string[] allowed)
    {
        foreach (var child in Elements(element))
        {
            if (child.NamespaceURI != EbmsNamespaces.Eb || Array.IndexOf(allowed, child.LocalName) < 0)
                throw Invalid(path, $"unexpected element {{{child.NamespaceURI}}}{child.LocalName}");
        }
    }

    private static XmlElement One(XmlElement parent, string localName, string ns, string path) =>
        Optional(parent, localName, path, ns) ?? throw Invalid(path, $"{localName} is missing");

    private static XmlElement? Optional(XmlElement parent, string localName, string path, string ns = EbmsNamespaces.Eb)
    {
        XmlElement? found = null;
        foreach (var child in Elements(parent))
        {
            if (child.LocalName != localName || child.NamespaceURI != ns) continue;
            if (found is not null)
                throw Invalid(path, $"{localName} appears more than once");
            found = child;
        }
        return found;
    }

    private static List<XmlElement> Many(XmlElement parent, string localName, string path, bool atLeastOne)
    {
        var found = Elements(parent).Where(e => e.LocalName == localName && e.NamespaceURI == EbmsNamespaces.Eb).ToList();
        if (atLeastOne && found.Count == 0)
            throw Invalid(path, $"{localName} is missing");
        return found;
    }

    private static string Text(XmlElement element, string path)
    {
        var text = element.InnerText.Trim();
        return text.Length > 0 ? text : throw Invalid(path, "is empty");
    }

    private static string? AttributeOrNull(XmlElement element, string name) =>
        element.GetAttributeNode(name) is { } attribute ? attribute.Value : null;

    private static As4ProcessingException Invalid(string path, string what) =>
        new(As4ErrorCode.InvalidHeader, $"eb:Messaging {path}: {what}.");
}
