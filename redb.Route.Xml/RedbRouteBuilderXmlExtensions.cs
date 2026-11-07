using Microsoft.Extensions.DependencyInjection;
using redb.Route.Core;
using redb.Route.Extensions;

namespace redb.Route.Xml;

/// <summary>
/// The DI surface (Ф2 §5): the same three loading overloads plus <c>context.xml</c> on
/// <see cref="RedbRouteBuilder"/>, applied at context-start time through the standard
/// <see cref="IRouteContextConfigurator"/> hook — an ordinary ASP.NET host loads XML routes
/// with one line inside <c>AddRedbRoute(...)</c>, no Tsak required.
/// </summary>
public static class RedbRouteBuilderXmlExtensions
{
    /// <summary>Loads route files by a glob pattern (or one wildcard-free path) at context start.</summary>
    public static RedbRouteBuilder AddXmlRoutes(this RedbRouteBuilder self, string globPattern,
        XmlRouteLoaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(self);
        ArgumentException.ThrowIfNullOrWhiteSpace(globPattern);
        return Configure(self, context => context.AddXmlRoutes(globPattern, options));
    }

    /// <summary>Loads the named route files at context start.</summary>
    public static RedbRouteBuilder AddXmlRoutes(this RedbRouteBuilder self, params string[] filePaths)
    {
        ArgumentNullException.ThrowIfNull(self);
        if (filePaths.Length == 0)
            throw new ArgumentException("At least one route file path is required.", nameof(filePaths));
        return Configure(self, context => context.AddXmlRoutes(filePaths));
    }

    /// <summary>Parses the XML text at context start — tests and generated hosts.</summary>
    public static RedbRouteBuilder AddXmlRoutesFromContent(this RedbRouteBuilder self, string xml,
        string? sourceName = null, XmlRouteLoaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(self);
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        return Configure(self, context => context.AddXmlRoutesFromContent(xml, sourceName, options));
    }

    /// <summary>Loads a <c>context.xml</c> (components, context beans, onInit) at context start.</summary>
    public static RedbRouteBuilder AddXmlContext(this RedbRouteBuilder self, string filePath,
        XmlRouteLoaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(self);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return Configure(self, context => context.AddXmlContext(filePath, options));
    }

    private static RedbRouteBuilder Configure(RedbRouteBuilder self, Action<RouteContext> apply)
    {
        self.Services.AddSingleton<IRouteContextConfigurator>(new XmlLoadConfigurator(apply));
        return self;
    }

    private sealed class XmlLoadConfigurator(Action<RouteContext> apply) : IRouteContextConfigurator
    {
        public void Configure(RouteContext context) => apply(context);
    }
}
