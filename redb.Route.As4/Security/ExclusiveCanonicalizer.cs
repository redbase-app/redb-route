using System.Security.Cryptography.Xml;
using System.Xml;

namespace redb.Route.As4.Security;

/// <summary>
/// Exclusive XML canonicalization (W3C xml-exc-c14n) of one element as it stands in its document, done by the
/// public <see cref="XmlDsigExcC14NTransform"/> of .NET. The element is copied into a document of its own
/// together with the namespace declarations in scope at its position; exclusive canonicalization then emits
/// only the ones the subtree visibly uses and the ones named in the inclusive prefix list — which is exactly
/// the output a signer computed over the element in place.
/// </summary>
internal static class ExclusiveCanonicalizer
{
    private const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";

    /// <summary>The canonical octets of <paramref name="element"/>.</summary>
    /// <param name="element">The element, still inside its document.</param>
    /// <param name="inclusivePrefixes">The <c>ec:InclusiveNamespaces/@PrefixList</c>, or null.</param>
    public static byte[] Canonicalize(XmlElement element, string? inclusivePrefixes)
    {
        ArgumentNullException.ThrowIfNull(element);

        var copy = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        var root = (XmlElement)copy.ImportNode(element, deep: true);
        copy.AppendChild(root);

        // Nearest declaration wins: walk outwards and add only prefixes the copy does not declare yet.
        for (var node = element.ParentNode; node is XmlElement ancestor; node = ancestor.ParentNode)
        {
            foreach (XmlAttribute attribute in ancestor.Attributes)
            {
                if (attribute.NamespaceURI != XmlnsNamespace) continue;
                if (root.HasAttribute(attribute.Name)) continue;
                root.SetAttribute(attribute.Name, attribute.Value);
            }
        }

        var transform = string.IsNullOrWhiteSpace(inclusivePrefixes)
            ? new XmlDsigExcC14NTransform()
            : new XmlDsigExcC14NTransform(inclusivePrefixes);
        transform.LoadInput(copy);

        using var output = (Stream)transform.GetOutput(typeof(Stream));
        using var buffer = new MemoryStream();
        output.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Adds an explicit <c>xmlns:prefix</c> declaration to every element of the subtree that uses a prefix (in its
    /// name or an attribute's) not yet declared in scope. The DOM lets code create <c>wsu:Id</c> or
    /// <c>ds:SignedInfo</c> without one; the serializer then adds it on the way out. Canonicalizing before that
    /// would digest a different document than the receiver gets, so a signer declares first — on the same element
    /// the serializer would.
    /// </summary>
    public static void DeclareNamespaces(XmlElement root)
    {
        ArgumentNullException.ThrowIfNull(root);
        foreach (var element in root.SelectNodes("descendant-or-self::*")!.OfType<XmlElement>())
        {
            Declare(element, element.Prefix, element.NamespaceURI);
            foreach (var attribute in element.Attributes.OfType<XmlAttribute>().ToList())
            {
                if (attribute.Prefix.Length == 0 || attribute.Prefix == "xmlns" || attribute.Prefix == "xml") continue;
                Declare(element, attribute.Prefix, attribute.NamespaceURI);
            }
        }
    }

    private static void Declare(XmlElement element, string prefix, string ns)
    {
        if (ns.Length == 0) return;
        var name = prefix.Length == 0 ? "xmlns" : "xmlns:" + prefix;
        // The nearest declaration of the prefix decides: the same URI is already in scope; a different one is
        // shadowed here, as the serializer would do.
        for (XmlNode? node = element; node is XmlElement e; node = e.ParentNode)
        {
            if (!e.HasAttribute(name)) continue;
            if (e.GetAttribute(name) == ns) return;
            break;
        }
        element.SetAttribute(name, ns);
    }

    /// <summary>
    /// The inclusive prefix list of a <c>ds:CanonicalizationMethod</c> or <c>ds:Transform</c> of the exclusive
    /// algorithm, or null when it carries none.
    /// </summary>
    public static string? PrefixList(XmlElement method) =>
        method.ChildNodes.OfType<XmlElement>()
            .FirstOrDefault(e => e.LocalName == "InclusiveNamespaces" && e.NamespaceURI == WsSecurityNames.ExcC14N)
            ?.GetAttribute("PrefixList");
}
