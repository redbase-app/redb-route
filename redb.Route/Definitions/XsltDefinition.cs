using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Xslt;

namespace redb.Route.Definitions;

/// <summary>
/// Leaf definition that transforms the exchange body through an XSLT stylesheet loaded from a
/// <b>file</b> (Apache Camel <c>xslt:</c> parity). The stylesheet is compiled once when the route is
/// built and reused for every message. <c>xsl:import</c>/<c>xsl:include</c> resolve relative to the file.
/// </summary>
public sealed class XsltFileDefinition : ProcessorDefinition
{
    private readonly string _stylesheetPath;
    private readonly XsltOutput _output;
    private readonly bool _failOnNullBody;
    private readonly bool _allowTemplateFromHeader;

    /// <summary>Creates a file-based XSLT definition.</summary>
    public XsltFileDefinition(
        string stylesheetPath,
        XsltOutput output = XsltOutput.String,
        bool failOnNullBody = true,
        bool allowTemplateFromHeader = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stylesheetPath);
        _stylesheetPath = stylesheetPath;
        _output = output;
        _failOnNullBody = failOnNullBody;
        _allowTemplateFromHeader = allowTemplateFromHeader;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        var engines = context.GetXsltEngineFactory();
        // Through the context's resource resolver, as the xslt: endpoint and validateXsd do: a stylesheet inside a package
        // is found where the package keeps its resources, not under the worker's working directory.
        string Resolve(string path) => ResourceResolution.Resolve(context, path, "XSLT stylesheet");
        return new XsltProcessor(
            engines.FromFile(Resolve(_stylesheetPath)),
            _output, _failOnNullBody,
            allowTemplateFromHeader: _allowTemplateFromHeader,
            fileEngineFactory: path => engines.FromFile(Resolve(path)),
            contentEngineFactory: engines.FromContent);
    }
}

/// <summary>
/// Leaf definition that transforms the exchange body through an <b>inline</b> XSLT stylesheet document
/// (self-contained; no <c>xsl:import</c>/<c>xsl:include</c> base URI). Compiled once at route build.
/// </summary>
public sealed class XsltContentDefinition : ProcessorDefinition
{
    private readonly string _stylesheetXml;
    private readonly XsltOutput _output;
    private readonly bool _failOnNullBody;
    private readonly bool _allowTemplateFromHeader;

    /// <summary>Creates an inline-stylesheet XSLT definition.</summary>
    public XsltContentDefinition(
        string stylesheetXml,
        XsltOutput output = XsltOutput.String,
        bool failOnNullBody = true,
        bool allowTemplateFromHeader = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stylesheetXml);
        _stylesheetXml = stylesheetXml;
        _output = output;
        _failOnNullBody = failOnNullBody;
        _allowTemplateFromHeader = allowTemplateFromHeader;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        var engines = context.GetXsltEngineFactory();
        return new XsltProcessor(
            engines.FromContent(_stylesheetXml),
            _output, _failOnNullBody,
            allowTemplateFromHeader: _allowTemplateFromHeader,
            fileEngineFactory: engines.FromFile,
            contentEngineFactory: engines.FromContent);
    }
}
