using System.Xml.Linq;

namespace redb.Route.Soap;

/// <summary>
/// WS-Security helpers. Ф4a: UsernameToken (pure XML, common and interoperable). XML-Signature and
/// XML-Encryption of the envelope (BinarySecurityToken / EncryptedData, full WSS policy) are Ф4b — a
/// dedicated effort validated against a real stack, using <c>System.Security.Cryptography.Xml</c>.
/// </summary>
internal static class SoapSecurity
{
    public const string WsseNs = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    public const string PasswordTextType = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordText";

    /// <summary>Builds a <c>&lt;wsse:Security&gt;</c> header carrying a UsernameToken (as an XML string).</summary>
    public static string BuildUsernameTokenHeader(string username, string? password)
    {
        XNamespace wsse = WsseNs;
        var token = new XElement(wsse + "UsernameToken",
            new XElement(wsse + "Username", username));
        if (password is not null)
            token.Add(new XElement(wsse + "Password", new XAttribute("Type", PasswordTextType), password));

        var security = new XElement(wsse + "Security",
            new XAttribute(XNamespace.Xmlns + "wsse", WsseNs),
            token);
        return security.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>Extracts (username, password) from a <c>&lt;wsse:Security&gt;</c> element, or null if absent.</summary>
    public static (string Username, string? Password)? ReadUsernameToken(XElement security)
    {
        XNamespace wsse = WsseNs;
        var token = security.Element(wsse + "UsernameToken");
        var user = token?.Element(wsse + "Username")?.Value;
        if (string.IsNullOrEmpty(user)) return null;
        var pass = token!.Element(wsse + "Password")?.Value;
        return (user, pass);
    }

    /// <summary>Whether an envelope header element is the <c>&lt;wsse:Security&gt;</c> element.</summary>
    public static bool IsSecurityHeader(XElement element) =>
        element.Name.LocalName == "Security" && element.Name.NamespaceName == WsseNs;
}
