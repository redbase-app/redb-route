using System.Xml.Linq;

namespace redb.Route.Soap;

/// <summary>
/// WSDL publishing for a SOAP consumer (camel-cxf <c>?wsdl</c> parity). .NET Core has no runtime WSDL
/// <em>import</em> (by design), but <em>publishing</em> a static contract is straightforward: a consumer whose
/// <see cref="SoapConnectionFactory.Wsdl"/> is set serves it on <c>GET …?wsdl</c>, with the
/// <c>&lt;soap:address&gt;</c> location rewritten to the address the client actually reached us on. The WSDL
/// itself is authored (or <c>dotnet-svcutil</c>-derived), not generated from types — the honest boundary.
/// </summary>
internal static class SoapWsdl
{
    /// <summary>Resolves the configured WSDL: inline XML as-is, otherwise a file path to read.</summary>
    public static string? Load(string? wsdlPathOrXml)
    {
        if (string.IsNullOrWhiteSpace(wsdlPathOrXml)) return null;
        var trimmed = wsdlPathOrXml.TrimStart();
        if (trimmed.StartsWith('<')) return wsdlPathOrXml;              // inline WSDL XML
        // Looks like a path: read it, or return null (⇒ 404) rather than serving a bogus path string as WSDL.
        return File.Exists(wsdlPathOrXml) ? File.ReadAllText(wsdlPathOrXml) : null;
    }

    /// <summary>
    /// Rewrites every <c>&lt;soap:address&gt;</c> / <c>&lt;soap12:address&gt;</c> <c>location</c> to
    /// <paramref name="endpointUrl"/> so a client that fetched the WSDL posts back to the real address.
    /// Best-effort: malformed WSDL is returned unchanged.
    /// </summary>
    public static string RewriteAddress(string wsdlXml, string endpointUrl)
    {
        try
        {
            var doc = XDocument.Parse(wsdlXml);
            var addresses = doc.Descendants()
                .Where(e => e.Name.LocalName == "address"
                            && e.Name.NamespaceName.Contains("wsdl/soap", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (addresses.Count == 0) return wsdlXml;
            foreach (var addr in addresses)
                addr.SetAttributeValue("location", endpointUrl);
            return doc.Declaration is null ? doc.ToString() : doc.Declaration + Environment.NewLine + doc.ToString();
        }
        catch
        {
            return wsdlXml;
        }
    }
}
