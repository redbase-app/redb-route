using redb.Route.Abstractions;

namespace SerialNumbers.Xml;

/// <summary>
/// The module bootstrap Tsak calls before the artifacts load (<c>main(IRouteContext)</c>). It does
/// what markup cannot: AS2 certificates are X509 objects loaded from files, the demo database is
/// created and seeded, and the partner list comes from redb. Everything that IS expressible -
/// components, the sql: data source, the SFTP partner connection and the
/// beans - is declared in context.xml instead, so the file says what the module is made of.
/// </summary>
public static class InitRoute
{
    public static Task main(IRouteContext context) => Core.InitRoute.main(context);
}
