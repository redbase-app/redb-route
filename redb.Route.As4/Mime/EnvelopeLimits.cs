using redb.Route.Core;

namespace redb.Route.As4.Mime;

/// <summary>
/// The structure an AS4 envelope may have, received or answered: tighter than <see cref="XmlLimits.Default"/>,
/// because an eDelivery envelope is shallow and narrow whoever builds it. Measured on 3 000 envelopes of Holodeck B2B
/// 8.1, Domibus 5 (Harmony 2.6) and this connector, messages and signals alike: at most 10 levels deep, 8 attributes
/// and 8 namespace declarations on an element, 24 namespace declarations in the envelope. The limits keep two to four
/// times that; namespace declarations count as attributes, so their own limit sits below the attribute limit. The whole
/// envelope may declare far more, for messages with many payloads (each adds its references and encryption elements).
/// The size is the endpoint's <c>maxEnvelopeCharacters</c>.
/// <para>
/// They are the profile's shape, not a deployment setting: an envelope over them is not one any eDelivery access point
/// sends, and it is refused before canonicalization or a signature check spends anything on it.
/// </para>
/// </summary>
internal static class EnvelopeLimits
{
    /// <summary>Nesting depth of the envelope's elements.</summary>
    internal const int MaxDepth = 32;

    /// <summary>Attributes on one element, namespace declarations included.</summary>
    internal const int MaxAttributesPerElement = 32;

    /// <summary>Namespace declarations on one element.</summary>
    internal const int MaxNamespaceDeclarationsPerElement = 16;

    /// <summary>Namespace declarations in the whole envelope.</summary>
    internal const int MaxNamespaceDeclarations = 4096;

    /// <summary>The limits of an envelope of at most <paramref name="maxEnvelopeCharacters"/> characters.</summary>
    internal static XmlLimits For(long maxEnvelopeCharacters) => new()
    {
        MaxDepth = MaxDepth,
        MaxAttributesPerElement = MaxAttributesPerElement,
        MaxNamespaceDeclarationsPerElement = MaxNamespaceDeclarationsPerElement,
        MaxNamespaceDeclarations = MaxNamespaceDeclarations,
        MaxCharacters = maxEnvelopeCharacters,
    };
}
