using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using redb.Route.Abstractions;

namespace redb.Route.Xslt;

/// <summary>
/// Compiles stylesheets into <see cref="IXsltEngine"/> instances — the point at which a different
/// XSLT processor is substituted for the built-in one.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IXsltEngine"/> has always been an interface, and its documentation has always said a
/// Saxon-backed engine could be added later in an adapter package. It could not: every construction
/// site named <see cref="XslCompiledTransformEngine"/> directly, so there was nothing to substitute.
/// An interface with no seam behind it is documentation that lies.
/// </para>
/// <para>
/// Both methods are here rather than in <see cref="IXsltEngine"/> because the engine is a compiled
/// stylesheet, and compiling is exactly what a replacement processor does differently. A route that
/// allows a per-message stylesheet needs to compile again at runtime, which is why the processor
/// holds the factory and not just the engine.
/// </para>
/// <para>
/// The default stays the BCL, XSLT 1.0, no dependencies — the same behaviour a route gets today.
/// XSLT 2.0/3.0 needs a processor we cannot ship (see <c>docs/V4/12-XQUERY.md</c>): every .NET
/// implementation is commercial. What we can give is the contract, so someone who has bought one
/// registers it in their own project, under their own licence.
/// </para>
/// </remarks>
public interface IXsltEngineFactory
{
    /// <summary>
    /// Compiles a stylesheet from a file path or URL, which also becomes the base URI for
    /// <c>xsl:import</c> and <c>xsl:include</c>.
    /// </summary>
    IXsltEngine FromFile(string stylesheetPath);

    /// <summary>
    /// Compiles a self-contained stylesheet document. There is no base URI, so
    /// <c>xsl:import</c>/<c>xsl:include</c> have nothing to resolve against.
    /// </summary>
    IXsltEngine FromContent(string stylesheetXml);
}

/// <summary>
/// The built-in factory: BCL <see cref="System.Xml.Xsl.XslCompiledTransform"/>, XSLT 1.0, no
/// external dependencies. Used whenever a route has registered nothing else.
/// </summary>
public sealed class XslCompiledTransformEngineFactory : IXsltEngineFactory
{
    /// <summary>The shared instance. The factory holds no state; the compiled stylesheets do.</summary>
    public static readonly XslCompiledTransformEngineFactory Instance = new();

    /// <inheritdoc />
    public IXsltEngine FromFile(string stylesheetPath) => XslCompiledTransformEngine.FromFile(stylesheetPath);

    /// <inheritdoc />
    public IXsltEngine FromContent(string stylesheetXml) => XslCompiledTransformEngine.FromContent(stylesheetXml);
}

/// <summary>
/// Registration and resolution of the XSLT engine factory, following the same order as every other
/// pluggable service here: what the context was told, then the DI container, then the default.
/// </summary>
public static class XsltEngineRegistration
{
    /// <summary>
    /// Registers an XSLT engine factory on a context built without DI:
    /// <c>context.UseXsltEngine(new SaxonXsltEngineFactory())</c>.
    /// </summary>
    public static IRouteContext UseXsltEngine(this IRouteContext context, IXsltEngineFactory factory)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(factory);
        context.AddService(typeof(IXsltEngineFactory), factory);
        return context;
    }

    /// <summary>
    /// DI registration: <c>services.AddXsltEngine(new SaxonXsltEngineFactory())</c>.
    /// </summary>
    public static IServiceCollection AddXsltEngine(this IServiceCollection services, IXsltEngineFactory factory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(factory);
        services.TryAddSingleton(factory);
        return services;
    }

    /// <summary>
    /// Resolves the factory a route should compile its stylesheets with.
    /// </summary>
    /// <remarks>
    /// A null context is not an error: definitions are built in tests and tools without one, and
    /// the answer there is the built-in engine, which is also the answer when nothing is registered.
    /// </remarks>
    public static IXsltEngineFactory GetXsltEngineFactory(this IRouteContext? context)
        => context?.GetService<IXsltEngineFactory>()
           ?? context?.GetServiceProvider()?.GetService(typeof(IXsltEngineFactory)) as IXsltEngineFactory
           ?? XslCompiledTransformEngineFactory.Instance;
}
