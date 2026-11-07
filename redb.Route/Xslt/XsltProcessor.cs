using System.Collections.Concurrent;
using redb.Route.Abstractions;

namespace redb.Route.Xslt;

/// <summary>
/// Pipeline processor that transforms the exchange body through an <see cref="IXsltEngine"/> — the
/// transformation counterpart of <see cref="redb.Route.Validation.ValidateProcessor"/>. The result
/// (string / bytes / DOM, per <see cref="XsltOutput"/>) replaces <c>In.Body</c>. The
/// <c>Content-Type</c> header is left unchanged (matching Apache Camel).
/// <para>
/// By default all message headers and exchange properties are passed as stylesheet parameters
/// (<c>xsl:param</c>), like Camel. When <c>allowTemplateFromHeader</c> is enabled a per-message
/// stylesheet from <see cref="XsltHeaders.ResourceUri"/> / <see cref="XsltHeaders.Stylesheet"/>
/// overrides the compiled default; those dynamic stylesheets are compiled once and cached.
/// </para>
/// </summary>
public sealed class XsltProcessor : IProcessor
{
    private readonly IXsltEngine _defaultEngine;
    private readonly XsltOutput _output;
    private readonly bool _failOnNullBody;
    private readonly bool _passHeadersAsParameters;
    private readonly bool _allowTemplateFromHeader;
    private readonly Func<string, IXsltEngine>? _fileEngineFactory;
    private readonly Func<string, IXsltEngine>? _contentEngineFactory;
    private readonly ConcurrentDictionary<string, IXsltEngine> _dynamicEngines = new(StringComparer.Ordinal);

    /// <summary>Creates an XSLT processor around a compiled engine.</summary>
    /// <param name="engine">The default (compile-once) transformation engine.</param>
    /// <param name="output">The result form (default <see cref="XsltOutput.String"/>).</param>
    /// <param name="failOnNullBody">Throw when the input body is null (Camel default: true).</param>
    /// <param name="passHeadersAsParameters">Pass headers/properties as stylesheet parameters (default: true).</param>
    /// <param name="allowTemplateFromHeader">Allow a per-message stylesheet from headers (default: false).</param>
    /// <param name="fileEngineFactory">Compiles an engine from a stylesheet path (for the dynamic path).</param>
    /// <param name="contentEngineFactory">Compiles an engine from inline stylesheet content (for the dynamic path).</param>
    public XsltProcessor(
        IXsltEngine engine,
        XsltOutput output = XsltOutput.String,
        bool failOnNullBody = true,
        bool passHeadersAsParameters = true,
        bool allowTemplateFromHeader = false,
        Func<string, IXsltEngine>? fileEngineFactory = null,
        Func<string, IXsltEngine>? contentEngineFactory = null)
    {
        _defaultEngine = engine ?? throw new ArgumentNullException(nameof(engine));
        _output = output;
        _failOnNullBody = failOnNullBody;
        _passHeadersAsParameters = passHeadersAsParameters;
        _allowTemplateFromHeader = allowTemplateFromHeader;
        _fileEngineFactory = fileEngineFactory;
        _contentEngineFactory = contentEngineFactory;
    }

    /// <inheritdoc />
    public Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var body = exchange.In.Body;
        if (body is null)
        {
            if (_failOnNullBody)
                throw new InvalidOperationException("XSLT: the input body is null (failOnNullBody=true).");
            return Task.CompletedTask;
        }

        var engine = ResolveEngine(exchange);
        var parameters = _passHeadersAsParameters ? XsltParameters.FromExchange(exchange) : null;
        exchange.In.Body = engine.Transform(body, _output, parameters);
        return Task.CompletedTask;
    }

    private IXsltEngine ResolveEngine(IExchange exchange)
    {
        if (!_allowTemplateFromHeader)
            return _defaultEngine;

        if (_fileEngineFactory is not null &&
            exchange.In.Headers.TryGetValue(XsltHeaders.ResourceUri, out var uriObj) &&
            uriObj is string uri && !string.IsNullOrWhiteSpace(uri))
        {
            return _dynamicEngines.GetOrAdd("uri:" + uri, _ => _fileEngineFactory(uri));
        }

        if (_contentEngineFactory is not null &&
            exchange.In.Headers.TryGetValue(XsltHeaders.Stylesheet, out var xmlObj) &&
            xmlObj is string xml && !string.IsNullOrWhiteSpace(xml))
        {
            return _dynamicEngines.GetOrAdd("xml:" + xml, _ => _contentEngineFactory(xml));
        }

        return _defaultEngine;
    }
}
