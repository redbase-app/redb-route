using redb.Route.Abstractions;

namespace redb.Route.Templates;

/// <summary>Runtime side of <see cref="PayloadTemplateDefinition"/>: renders and writes the result to its target.</summary>
internal sealed class PayloadTemplateProcessor : IProcessor
{
    private readonly CompiledTemplate _compiled;
    private readonly MediaType _mediaType;
    private readonly TemplateTarget _target;
    private readonly string? _targetName;
    private readonly TemplateArgs? _args;
    private readonly RouteTemplateOptions _options;

    public PayloadTemplateProcessor(CompiledTemplate compiled, MediaType mediaType, TemplateTarget target, string? targetName, TemplateArgs? args, RouteTemplateOptions options)
    {
        _compiled = compiled;
        _mediaType = mediaType;
        _target = target;
        _targetName = targetName;
        _args = args;
        _options = options;
    }

    public Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var text = TemplateEngine.Render(_compiled, exchange, _mediaType, _args, _options);
        switch (_target)
        {
            case TemplateTarget.Header:
                exchange.In.Headers[_targetName!] = text;
                break;
            case TemplateTarget.Property:
                exchange.Properties[_targetName!] = text;
                break;
            default:
                exchange.In.Body = text;
                exchange.In.ContentType = ContentTypeOf(_mediaType);
                break;
        }
        return Task.CompletedTask;
    }

    /// <summary>Content type written to the message when the target is the body.</summary>
    public static string ContentTypeOf(MediaType mediaType) => mediaType switch
    {
        MediaType.Json => "application/json",
        MediaType.Xml => "application/xml",
        _ => "text/plain",
    };
}
