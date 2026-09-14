using System.Xml;

namespace SerialNumbers.Core.Integration.Xml;

/// <summary>Reads just enough of an incoming file to route it.</summary>
public static class XmlMessageInspector
{
    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
    };

    /// <summary>
    /// The local name of the root element, or <c>null</c> when the content is not well-formed
    /// XML. The <c>null</c> is the answer, not a lost error: the intake route records such a file
    /// as Invalid, with this reason, and keeps it in the archive.
    /// <para>
    /// The whole document is read, not just its start: a file cut off at the end has a perfectly
    /// good root element, and only reading to the last byte tells it from a valid one.
    /// </para>
    /// </summary>
    public static string? RootElementName(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        try
        {
            using var reader = XmlReader.Create(new StringReader(content), Settings);
            if (reader.MoveToContent() != XmlNodeType.Element)
                return null;

            var root = reader.LocalName;
            while (reader.Read())
            {
            }
            return root;
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
