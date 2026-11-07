using System.Runtime.CompilerServices;
using System.Xml;

namespace redb.Route.Expressions.Tokenizers;

/// <summary>
/// Splits XML body content by extracting elements matching the given local name.
/// Uses async <see cref="XmlReader"/> for streaming parse with XXE protection.
/// </summary>
internal static class XmlTokenizer
{
    /// <summary>Tokenizes XML body by extracting elements with the given name.</summary>
    /// <param name="body">Exchange body (Stream, string, or byte[]).</param>
    /// <param name="elementName">Local name of the elements to extract.</param>
    /// <param name="inheritNamespaceFrom">Optional parent element name whose namespaces should be injected.</param>
    /// <param name="ct">Cancellation token.</param>
    internal static async IAsyncEnumerable<object?> Tokenize(
        object? body, string elementName, string? inheritNamespaceFrom,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = CreateXmlReader(body);
        Dictionary<string, string>? parentNamespaces = null;

        var needRead = true;
        while (needRead ? await reader.ReadAsync().ConfigureAwait(false) : reader.ReadState == System.Xml.ReadState.Interactive)
        {
            needRead = true;
            ct.ThrowIfCancellationRequested();

            // Collect namespaces from parent element
            if (inheritNamespaceFrom != null
                && reader.NodeType == XmlNodeType.Element
                && reader.LocalName == inheritNamespaceFrom)
            {
                parentNamespaces = CollectNamespaces(reader);
            }

            // Found target element
            if (reader.NodeType == XmlNodeType.Element
                && reader.LocalName == elementName)
            {
                var outerXml = await reader.ReadOuterXmlAsync().ConfigureAwait(false);

                if (parentNamespaces is { Count: > 0 })
                    outerXml = InjectNamespaces(outerXml, parentNamespaces);

                yield return outerXml;
                // ReadOuterXmlAsync already positioned reader on next node
                needRead = false;
            }
        }
    }

    private static XmlReader CreateXmlReader(object? body)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit
        };

        return body switch
        {
            Stream s => XmlReader.Create(s, settings),
            string str => XmlReader.Create(new StringReader(str), settings),
            byte[] b => XmlReader.Create(new MemoryStream(b, writable: false), settings),
            _ => throw new InvalidOperationException(
                $"SplitXml: unsupported body type {body?.GetType().Name ?? "null"}")
        };
    }

    private static Dictionary<string, string> CollectNamespaces(XmlReader reader)
    {
        var ns = new Dictionary<string, string>();
        if (reader.HasAttributes)
        {
            for (int i = 0; i < reader.AttributeCount; i++)
            {
                reader.MoveToAttribute(i);
                if (reader.Prefix == "xmlns" || (reader.Prefix == "" && reader.LocalName == "xmlns"))
                    ns[reader.Name] = reader.Value;
            }
            reader.MoveToElement();
        }
        return ns;
    }

    private static string InjectNamespaces(string outerXml, Dictionary<string, string> namespaces)
    {
        var insertPos = outerXml.IndexOf('>');
        if (insertPos < 0) return outerXml;
        if (outerXml[insertPos - 1] == '/') insertPos--;

        var nsAttrs = string.Join(" ", namespaces
            .Where(kv => !outerXml.Contains(kv.Key))
            .Select(kv => $"{kv.Key}=\"{kv.Value}\""));

        if (nsAttrs.Length == 0) return outerXml;

        return outerXml.Insert(insertPos, " " + nsAttrs);
    }
}
