using System.Xml;

namespace redb.Route.As4.Messaging;

/// <summary>Namespaces of the ebMS 3.0 header and of the receipt's non-repudiation information.</summary>
internal static class EbmsNamespaces
{
    /// <summary>ebMS 3.0 core header.</summary>
    public const string Eb = "http://docs.oasis-open.org/ebxml-msg/ebms/v3.0/ns/core/200704/";

    /// <summary>ebBP signals: <c>ebbp:NonRepudiationInformation</c> inside an AS4 receipt.</summary>
    public const string Ebbp = "http://docs.oasis-open.org/ebxml-bp/ebbp-signals-2.0";

    /// <summary>SOAP 1.2 envelope.</summary>
    public const string Soap12 = "http://www.w3.org/2003/05/soap-envelope";

    /// <summary>Default message partition channel (Core §3.4.1).</summary>
    public const string DefaultMpc = "http://docs.oasis-open.org/ebxml-msg/ebms/v3.0/ns/core/200704/defaultMPC";
}

/// <summary>The content of one <c>eb:Messaging</c> header: user messages and signals.</summary>
/// <param name="UserMessages">At most one in AS4 (profile §2.1); a list because the header allows more.</param>
/// <param name="SignalMessages">Receipts, errors, pull requests; one header may carry several.</param>
internal sealed record EbmsMessaging(IReadOnlyList<UserMessage> UserMessages, IReadOnlyList<SignalMessage> SignalMessages);

/// <summary><c>eb:MessageInfo</c>.</summary>
internal sealed record MessageInfo(DateTimeOffset Timestamp, string MessageId, string? RefToMessageId);

/// <summary><c>eb:PartyId</c> and its optional <c>@type</c>.</summary>
internal sealed record PartyId(string Value, string? Type);

/// <summary><c>eb:From</c> or <c>eb:To</c>: the party ids and the role.</summary>
internal sealed record Party(IReadOnlyList<PartyId> PartyIds, string Role);

/// <summary><c>eb:AgreementRef</c>.</summary>
internal sealed record AgreementRef(string Value, string? Type, string? PMode);

/// <summary><c>eb:Service</c>.</summary>
internal sealed record Service(string Value, string? Type);

/// <summary><c>eb:CollaborationInfo</c>.</summary>
internal sealed record CollaborationInfo(AgreementRef? AgreementRef, Service Service, string Action, string ConversationId);

/// <summary><c>eb:Property</c> of message or part properties.</summary>
internal sealed record Property(string Name, string Value, string? Type);

/// <summary><c>eb:PartInfo</c>: a payload reference (<c>cid:</c> for an attachment, none for the SOAP body) and its properties.</summary>
internal sealed record PartInfo(string? Href, IReadOnlyList<Property> Properties)
{
    /// <summary>The value of the part property <paramref name="name"/>, or null.</summary>
    public string? PropertyValue(string name) => Properties.FirstOrDefault(p => p.Name == name)?.Value;
}

/// <summary><c>eb:UserMessage</c>.</summary>
internal sealed record UserMessage(
    MessageInfo MessageInfo,
    Party From,
    Party To,
    CollaborationInfo CollaborationInfo,
    IReadOnlyList<Property> MessageProperties,
    IReadOnlyList<PartInfo> PayloadInfo,
    string? Mpc)
{
    /// <summary>The value of the message property <paramref name="name"/>, or null.</summary>
    public string? PropertyValue(string name) => MessageProperties.FirstOrDefault(p => p.Name == name)?.Value;
}

/// <summary>
/// <c>eb:SignalMessage</c>. <see cref="ReceiptContent"/> keeps the child elements of <c>eb:Receipt</c> as they
/// arrived (the non-repudiation information is compared element by element against what was signed).
/// </summary>
internal sealed record SignalMessage(
    MessageInfo MessageInfo,
    IReadOnlyList<XmlElement>? ReceiptContent,
    IReadOnlyList<EbmsError> Errors,
    string? PullRequestMpc)
{
    /// <summary>Whether the signal is a receipt.</summary>
    public bool IsReceipt => ReceiptContent is not null;
}

/// <summary><c>eb:Error</c>.</summary>
internal sealed record EbmsError(
    string ErrorCode,
    As4ErrorSeverity Severity,
    string? Category,
    string? Origin,
    string? RefToMessageInError,
    string? ShortDescription,
    string? Description,
    string? ErrorDetail);
