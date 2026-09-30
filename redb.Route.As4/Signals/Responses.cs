using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using redb.Route.As4.Messaging;
using redb.Route.As4.Mime;
using redb.Route.As4.Security;

namespace redb.Route.As4.Signals;

/// <summary>
/// What a receiver answers in the HTTP response (eDelivery AS4 1.16: receipts and errors only as responses):
/// a signed non-repudiation receipt, an ebMS error, or — for a message that is not ebMS at all — a SOAP fault.
/// Statuses follow what Holodeck B2B answers: an ebMS signal with 200; a SOAP fault per the SOAP 1.2 HTTP
/// binding, 400 for a <c>Sender</c> fault, 500 for <c>MustUnderstand</c>.
/// </summary>
internal static class Responses
{
    /// <summary>The signed receipt for <paramref name="receivedMessageId"/>, echoing <paramref name="receivedReferences"/>.</summary>
    public static (string ContentType, byte[] Body) Receipt(string receivedMessageId, IReadOnlyList<XmlElement> receivedReferences,
        string host, X509Certificate2 signer, As4KeyReference keyReference, TimeSpan? timestampTtl)
    {
        var signal = new SignalMessage(
            new MessageInfo(DateTimeOffset.UtcNow, MessageIdFactory.New(host), receivedMessageId),
            Receipts.NonRepudiation(receivedReferences), [], null);
        var message = SwaMessage.Create(MessagingWriter.CreateEnvelope(signal));
        As4SecurityEngine.Sign(message, signer, keyReference, timestampTtl, DateTimeOffset.UtcNow);
        return message.Write();
    }

    /// <summary>
    /// An ebMS error signal for <paramref name="refToMessageId"/> (null when the message could not be read far
    /// enough to know it). The texts are the code's fixed partner text and a reference to quote — never the text of
    /// one of our exceptions (BR-4); <paramref name="callerText"/> is the one exception: a description of the caller's
    /// own bytes. Signed when a signer is given (the agreement is known), unsigned otherwise.
    /// </summary>
    public static (string ContentType, byte[] Body) Error(As4ErrorCode code, string? refToMessageId, string host, string reference,
        string? callerText = null, X509Certificate2? signer = null, As4KeyReference keyReference = As4KeyReference.BinarySecurityToken)
    {
        var error = new EbmsError(code.Code, code.Severity, code.Category, "ebms", refToMessageId, code.ShortDescription,
            callerText ?? code.PartnerText, $"ref: {reference}");
        var signal = new SignalMessage(new MessageInfo(DateTimeOffset.UtcNow, MessageIdFactory.New(host), refToMessageId), null, [error], null);
        var message = SwaMessage.Create(MessagingWriter.CreateEnvelope(signal));
        if (signer is not null)
            As4SecurityEngine.Sign(message, signer, keyReference, timestampTtl: null, DateTimeOffset.UtcNow);
        return message.Write();
    }

    /// <summary>A SOAP 1.2 fault with code <c>soapenv:Sender</c> or <c>soapenv:MustUnderstand</c>.</summary>
    public static (int Status, string ContentType, byte[] Body) SoapFault(bool mustUnderstand, string reason)
    {
        var code = mustUnderstand ? "MustUnderstand" : "Sender";
        var xml =
            $"<soapenv:Envelope xmlns:soapenv=\"{EbmsNamespaces.Soap12}\"><soapenv:Body><soapenv:Fault>" +
            $"<soapenv:Code><soapenv:Value>soapenv:{code}</soapenv:Value></soapenv:Code>" +
            $"<soapenv:Reason><soapenv:Text xml:lang=\"en\">{System.Security.SecurityElement.Escape(reason)}</soapenv:Text></soapenv:Reason>" +
            "</soapenv:Fault></soapenv:Body></soapenv:Envelope>";
        return (mustUnderstand ? 500 : 400, "application/soap+xml; charset=UTF-8", Encoding.UTF8.GetBytes(xml));
    }
}
