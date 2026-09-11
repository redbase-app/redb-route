using redb.Route.Abstractions;
using redb.Route.Definitions;
using redb.Route.Processors;

namespace redb.Route.Http.Rest;

/// <summary>
/// First step of a REST route: path parameters become plain headers (<c>header.id</c>), query parameters
/// <c>header.query.*</c>; a request whose <c>Content-Type</c> does not match <c>Consumes</c> is answered
/// with 415 and stopped; under JSON binding a declared request type is unmarshalled.
/// </summary>
public sealed class RestRequestDefinition : ProcessorDefinition
{
    private readonly RestVerbDefinition _verb;
    private readonly RestOptions _options;

    internal RestRequestDefinition(RestVerbDefinition verb, RestOptions options)
    {
        _verb = verb;
        _options = options;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        var consumes = _verb.EffectiveConsumes(_options);
        var unmarshal = _verb.EffectiveBinding(_options) == RestBindingMode.Json && _verb.RequestType is not null
            ? context.GetService<IDataFormatRegistry>()?.GetSerializer(consumes ?? "application/json")
            : null;
        var requestType = _verb.RequestType;

        return new DelegateProcessor(async (exchange, ct) =>
        {
            var headers = exchange.In.Headers;
            var snapshot = headers.ToList();
            // Parameters come from the request line only. A client header that happens to be named
            // "query.role" must not survive as if it had come from the query string, so the plain
            // query.* names are cleared before the real parameters are written.
            foreach (var (key, _) in snapshot)
                if (key.StartsWith("query.", StringComparison.OrdinalIgnoreCase))
                    headers.Remove(key);
            foreach (var (key, value) in snapshot)
            {
                if (key.StartsWith(HttpHeaders.RouteParamPrefix, StringComparison.Ordinal))
                    headers[key[HttpHeaders.RouteParamPrefix.Length..]] = value;
                else if (key.StartsWith(HttpHeaders.QueryParamPrefix, StringComparison.Ordinal))
                    headers["query." + key[HttpHeaders.QueryParamPrefix.Length..]] = value;
            }

            if (consumes is not null && exchange.In.Body is not null && !MediaTypeMatches(exchange.In.ContentType, consumes))
            {
                exchange.In.Headers[HttpHeaders.ResponseCode] = 415;
                exchange.In.Headers[HttpHeaders.ResponseContentType] = "text/plain";
                exchange.In.ContentType = "text/plain";
                exchange.In.Body = $"Unsupported Media Type: expected {consumes}";
                exchange.Stop();
                return;
            }

            if (unmarshal is not null && requestType is not null && exchange.In.Body is byte[] bytes && bytes.Length > 0)
                exchange.In.Body = unmarshal.Deserialize(bytes, requestType);

            await Task.CompletedTask.ConfigureAwait(false);
        });
    }

    internal static bool MediaTypeMatches(string? actual, string expected)
    {
        if (string.IsNullOrWhiteSpace(actual)) return false;
        var actualBase = actual.Split(';')[0].Trim();
        var expectedBase = expected.Split(';')[0].Trim();
        return string.Equals(actualBase, expectedBase, StringComparison.OrdinalIgnoreCase)
            || (expectedBase.EndsWith("/json", StringComparison.OrdinalIgnoreCase) && actualBase.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
            || (expectedBase.EndsWith("/xml", StringComparison.OrdinalIgnoreCase) && actualBase.EndsWith("+xml", StringComparison.OrdinalIgnoreCase));
    }
}
