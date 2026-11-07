using redb.Route.Abstractions;
using redb.Route.Definitions;
using redb.Route.Processors;

namespace redb.Route.Http.Rest;

/// <summary>
/// Last step of a REST route: writes the <c>Produces</c> media type, marshals a CLR object body to
/// JSON under JSON binding, and answers 204 when the route left no body and set no status.
/// The status code itself is the HTTP consumer's contract: header <c>redbHttp.ResponseCode</c>.
/// </summary>
public sealed class RestResponseDefinition : ProcessorDefinition
{
    private readonly RestVerbDefinition _verb;
    private readonly RestOptions _options;

    internal RestResponseDefinition(RestVerbDefinition verb, RestOptions options)
    {
        _verb = verb;
        _options = options;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        var produces = _verb.EffectiveProduces(_options);
        var json = _verb.EffectiveBinding(_options) == RestBindingMode.Json
            ? context.GetService<IDataFormatRegistry>()?.GetSerializer(produces ?? "application/json")
            : null;

        return new DelegateProcessor(exchange =>
        {
            var message = exchange.Out ?? exchange.In;
            if (exchange.IsStopped) return;

            if (json is not null && message.Body is not (null or string or byte[] or Stream))
            {
                message.Body = json.Serialize(message.Body);
                message.ContentType = json.ContentType;
            }

            if (produces is not null && !message.Headers.ContainsKey(HttpHeaders.ResponseContentType))
                message.Headers[HttpHeaders.ResponseContentType] = produces;

            if (message.Body is null && !message.Headers.ContainsKey(HttpHeaders.ResponseCode))
                message.Headers[HttpHeaders.ResponseCode] = 204;
        });
    }
}
