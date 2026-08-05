using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Xslt;

// ── Component ────────────────────────────────────────────────────────────────

/// <summary>
/// Component for XSLT transformation (Apache Camel <c>xslt:</c> parity). Producer-only — you send
/// <b>to</b> it. Mirrors <see cref="redb.Route.Validation.ValidatorComponent"/>.
/// <para>
/// URI format: <c>xslt:path/to/stylesheet.xsl</c>. Parameters:
/// <list type="bullet">
///   <item><c>output</c> — result form: <c>string</c> (default) / <c>bytes</c> / <c>dom</c>.</item>
///   <item><c>failOnNullBody</c> — throw when the input body is null (default <c>true</c>).</item>
///   <item><c>allowTemplateFromHeader</c> — allow a per-message stylesheet from headers (default <c>false</c>).</item>
/// </list>
/// </para>
/// <para>
/// Registered out of the box under the <c>xslt</c> scheme, so <c>.To("xslt:...")</c> works without any
/// setup. All headers and exchange properties are passed to the stylesheet as parameters (Camel parity).
/// </para>
/// </summary>
public sealed class XsltComponent : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "xslt";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var options = new XsltEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.StylesheetPath = uri.Path;
        options.Validate();
        return new XsltEndpoint(uri, this, options);
    }
}

// ── Options ──────────────────────────────────────────────────────────────────

/// <summary>Options for the XSLT endpoint.</summary>
public sealed class XsltEndpointOptions : EndpointOptions
{
    /// <summary>Path to the stylesheet file (from the URI path).</summary>
    public string StylesheetPath { get; set; } = string.Empty;

    /// <summary>Result form: string (default) / bytes / dom.</summary>
    public XsltOutput Output { get; set; } = XsltOutput.String;

    /// <summary>Throw when the input body is null. Default: <c>true</c>.</summary>
    public bool FailOnNullBody { get; set; } = true;

    /// <summary>Allow a per-message stylesheet from the <c>CamelXsltResourceUri</c>/<c>CamelXsltStylesheet</c>
    /// headers to override the compiled default. Default: <c>false</c>.</summary>
    public bool AllowTemplateFromHeader { get; set; }

    /// <inheritdoc />
    public override void Validate()
    {
        if (string.IsNullOrWhiteSpace(StylesheetPath))
            throw new ArgumentException("Stylesheet path is required for the xslt component.");

        if (!File.Exists(StylesheetPath))
            throw new FileNotFoundException($"XSLT stylesheet not found: {StylesheetPath}");
    }
}

// ── Endpoint ─────────────────────────────────────────────────────────────────

/// <summary>Endpoint that transforms the body through a compiled XSLT stylesheet.</summary>
public sealed class XsltEndpoint : EndpointBase<XsltEndpointOptions>
{
    private readonly XsltProcessor _processor;

    internal XsltEndpoint(EndpointUri uri, XsltComponent component, XsltEndpointOptions options)
        : base(uri, component, options)
    {
        // Reuse XsltProcessor so the component gets parameters + dynamic-from-header behaviour for free.
        // The stylesheet is compiled once when the endpoint is created (Camel contentCache=true).
        _processor = new XsltProcessor(
            XslCompiledTransformEngine.FromFile(options.StylesheetPath),
            options.Output, options.FailOnNullBody,
            passHeadersAsParameters: true,
            allowTemplateFromHeader: options.AllowTemplateFromHeader,
            fileEngineFactory: XslCompiledTransformEngine.FromFile,
            contentEngineFactory: XslCompiledTransformEngine.FromContent);
    }

    internal XsltProcessor Processor => _processor;

    /// <inheritdoc />
    public override IProducer CreateProducer() => new XsltProducer(this);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
        => throw new NotSupportedException(
            "XSLT component does not support consuming (from). Use .To(\"xslt:...\") or the .Xslt(...) DSL.");
}

// ── Producer ─────────────────────────────────────────────────────────────────

/// <summary>Producer that runs the XSLT transform when the route sends to this endpoint.</summary>
public sealed class XsltProducer : IProducer
{
    private readonly XsltEndpoint _endpoint;

    internal XsltProducer(XsltEndpoint endpoint)
        => _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));

    /// <inheritdoc />
    public Task Process(IExchange exchange, CancellationToken ct = default)
        => _endpoint.Processor.Process(exchange, ct);

    /// <inheritdoc />
    public Task Start(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task Stop(CancellationToken ct = default) => Task.CompletedTask;
}
