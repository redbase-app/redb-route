using System.Xml;
using redb.Route.As4.Messaging;
using redb.Route.As4.Security;

namespace redb.Route.As4.Signals;

/// <summary>
/// AS4 non-repudiation receipts (AS4 profile §3.4, eDelivery AS4 1.16: <c>NonRepudiation = true</c>): the
/// content of <c>eb:Receipt</c> is one <c>ebbp:NonRepudiationInformation</c> holding a copy of every
/// <c>ds:Reference</c> of the signature of the received message. Built by the receiver; checked by the sender
/// against the references it signed, which proves the receiver got exactly those bytes.
/// </summary>
internal static class Receipts
{
    /// <summary>The <c>eb:Receipt</c> content for a message whose signature had <paramref name="receivedReferences"/>.</summary>
    public static IReadOnlyList<XmlElement> NonRepudiation(IReadOnlyList<XmlElement> receivedReferences)
    {
        ArgumentNullException.ThrowIfNull(receivedReferences);
        if (receivedReferences.Count == 0)
            throw new ArgumentException("A non-repudiation receipt needs the references of a signed message.", nameof(receivedReferences));

        var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        var information = doc.CreateElement("ebbp", "NonRepudiationInformation", EbmsNamespaces.Ebbp);
        information.SetAttribute("xmlns:ebbp", EbmsNamespaces.Ebbp);
        doc.AppendChild(information);

        foreach (var reference in receivedReferences)
        {
            var part = doc.CreateElement("ebbp", "MessagePartNRInformation", EbmsNamespaces.Ebbp);
            part.AppendChild(doc.ImportNode(reference, deep: true));
            information.AppendChild(part);
        }

        ExclusiveCanonicalizer.DeclareNamespaces(information);
        return [information];
    }

    /// <summary>
    /// Whether the receipt's non-repudiation information names exactly the references we signed — same URIs,
    /// same digest values, none missing, none extra. A receipt without it does not prove receipt.
    /// </summary>
    public static bool ProvesReceiptOf(SignalMessage receipt, IReadOnlyList<XmlElement> sentReferences)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(sentReferences);
        if (receipt.ReceiptContent is null)
            return false;

        var echoed = receipt.ReceiptContent
            .Where(e => e.LocalName == "NonRepudiationInformation" && e.NamespaceURI == EbmsNamespaces.Ebbp)
            .SelectMany(e => e.ChildNodes.OfType<XmlElement>())
            .Where(e => e.LocalName == "MessagePartNRInformation" && e.NamespaceURI == EbmsNamespaces.Ebbp)
            .SelectMany(e => e.ChildNodes.OfType<XmlElement>())
            .Where(e => e.LocalName == "Reference" && e.NamespaceURI == WsSecurityNames.Ds)
            .Select(Key)
            .ToList();

        var sent = sentReferences.Select(Key).ToList();
        return echoed.Count == sent.Count && new HashSet<string>(echoed, StringComparer.Ordinal).SetEquals(sent);
    }

    private static string Key(XmlElement reference)
    {
        var digest = reference.ChildNodes.OfType<XmlElement>()
            .FirstOrDefault(e => e.LocalName == "DigestValue" && e.NamespaceURI == WsSecurityNames.Ds)?.InnerText.Trim() ?? "";
        return reference.GetAttribute("URI") + "\n" + digest;
    }
}
