using System.Globalization;
using redb.Route.Abstractions;
using redb.Route.Definitions;
using redb.Route.Extensions;
using redb.Route.Processors;

namespace redb.Route.Http.Rest;

/// <summary>
/// First step of a REST route: path parameters become plain headers (<c>header.id</c>), query parameters
/// <c>header.query.*</c>; a request whose <c>Content-Type</c> does not match <c>Consumes</c> is answered
/// with 415 and stopped; with client request validation on, an <c>Accept</c> that excludes
/// <c>Produces</c> is answered with 406 and a missing or non-convertible declared parameter with 400;
/// under JSON binding a declared request type is unmarshalled.
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
        var produces = _verb.EffectiveProduces(_options);
        var validate = _verb.EffectiveValidation(_options);
        var parameters = _verb.Parameters.ToArray();
        var unmarshal = _verb.EffectiveBinding(_options) == RestBindingMode.Json && _verb.RequestType is not null
            ? context.GetService<IDataFormatRegistry>()?.GetSerializer(consumes ?? "application/json")
            : null;
        var requestType = _verb.RequestType;
        // Resolved here, while the routes are being built, so a misspelled name stops the start
        // instead of turning the first refused request into a 500.
        var errorHandler = _options.ErrorHandler is { } handlerName
            ? context.GetRequiredFromRegistry<IProcessor>(handlerName.StartsWith('#') ? handlerName[1..] : handlerName)
            : null;

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
                await Refuse(exchange, 415, $"Unsupported Media Type: expected {consumes}", null, errorHandler, ct).ConfigureAwait(false);
                return;
            }

            if (validate)
            {
                if (produces is not null && !Accepts(headers.TryGetValue("Accept", out var accept) ? accept?.ToString() : null, produces))
                {
                    await Refuse(exchange, 406, $"Not Acceptable: this operation produces {produces}", null, errorHandler, ct).ConfigureAwait(false);
                    return;
                }

                foreach (var parameter in parameters)
                {
                    if (CheckParameter(exchange, parameter) is { } reason)
                    {
                        await Refuse(exchange, 400, reason, parameter.Name, errorHandler, ct).ConfigureAwait(false);
                        return;
                    }
                }
            }

            if (unmarshal is not null && requestType is not null && exchange.In.Body is byte[] bytes && bytes.Length > 0)
                exchange.In.Body = unmarshal.Deserialize(bytes, requestType);
        });
    }

    private static async Task Refuse(IExchange exchange, int code, string reason, string? parameter,
        IProcessor? errorHandler, CancellationToken ct)
    {
        exchange.In.Headers[HttpHeaders.ResponseCode] = code;
        if (errorHandler is null)
        {
            exchange.In.Headers[HttpHeaders.ResponseContentType] = "text/plain";
            exchange.In.ContentType = "text/plain";
            exchange.In.Body = reason;
        }
        else
        {
            exchange.In.Body = null;
            exchange.Properties[RestErrorProperties.Code] = code;
            exchange.Properties[RestErrorProperties.Reason] = reason;
            if (parameter is not null)
                exchange.Properties[RestErrorProperties.Parameter] = parameter;
            await errorHandler.Process(exchange, ct).ConfigureAwait(false);
        }
        exchange.Stop();
    }

    /// <summary>The reason a declared parameter refuses the request, or <c>null</c> when it is fine.</summary>
    private static string? CheckParameter(IExchange exchange, RestParamDefinition parameter)
    {
        var label = parameter.Type switch
        {
            RestParamType.Path => "Path parameter",
            RestParamType.Query => "Query parameter",
            _ => "Header",
        };
        var raw = RawValue(exchange, parameter);
        if (raw is null)
            return parameter.Required ? $"{label} '{parameter.Name}' is required" : null;
        if (parameter.DataType == RestParamDataType.String)
            return null;

        // The HTTP consumer joins repeated query keys with "," and repeated headers with ", ";
        // every occurrence has to convert.
        foreach (var piece in raw.Split(','))
            if (!Converts(piece.Trim(), parameter.DataType))
                return $"{label} '{parameter.Name}' must be {Article(parameter.DataType)}, got '{raw}'";
        return null;
    }

    private static string? RawValue(IExchange exchange, RestParamDefinition parameter)
    {
        var headers = exchange.In.Headers;
        switch (parameter.Type)
        {
            case RestParamType.Path:
                return headers.TryGetValue(HttpHeaders.RouteParamPrefix + parameter.Name, out var path) ? path?.ToString() : null;
            case RestParamType.Query:
                return headers.TryGetValue(HttpHeaders.QueryParamPrefix + parameter.Name, out var query) ? query?.ToString() : null;
            default:
                // Only a header the client actually sent counts: path parameters and the query.* names
                // are written into the same header map by this step.
                var sent = exchange.Properties.TryGetValue("redbHttp.RequestHeaderNames", out var names)
                    && names is ISet<string> set && set.Contains(parameter.Name);
                return sent && headers.TryGetValue(parameter.Name, out var header) ? header?.ToString() : null;
        }
    }

    private static bool Converts(string value, RestParamDataType type) => type switch
    {
        RestParamDataType.Integer => long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _),
        RestParamDataType.Number => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && double.IsFinite(number),
        RestParamDataType.Boolean => value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("false", StringComparison.OrdinalIgnoreCase),
        _ => true,
    };

    private static string Article(RestParamDataType type) => type switch
    {
        RestParamDataType.Integer => "an integer",
        RestParamDataType.Number => "a number",
        RestParamDataType.Boolean => "true or false",
        _ => "a string",
    };

    /// <summary>
    /// Whether an <c>Accept</c> header admits <paramref name="produces"/> (RFC 9110 §12.5.1): no header
    /// admits everything; otherwise the most specific matching range decides (<c>type/subtype</c> over
    /// <c>type/*</c> over <c>*/*</c>) and <c>q=0</c> means "not acceptable". A range whose <c>q</c> does
    /// not parse admits nothing.
    /// </summary>
    internal static bool Accepts(string? accept, string produces)
    {
        if (string.IsNullOrWhiteSpace(accept)) return true;
        var produced = produces.Split(';')[0].Trim();
        var slash = produced.IndexOf('/');
        var producedType = slash > 0 ? produced[..slash] : produced;

        var bestSpecificity = -1;
        var bestQuality = 0.0;
        foreach (var part in accept.Split(','))
        {
            var segments = part.Split(';');
            var range = segments[0].Trim();
            if (range.Length == 0) continue;

            var specificity = range == "*/*" ? 0
                : range.EndsWith("/*", StringComparison.Ordinal) && string.Equals(range[..^2], producedType, StringComparison.OrdinalIgnoreCase) ? 1
                : string.Equals(range, produced, StringComparison.OrdinalIgnoreCase) ? 2
                : -1;
            if (specificity < bestSpecificity || specificity < 0) continue;

            var quality = 1.0;
            foreach (var parameter in segments.Skip(1))
            {
                var pair = parameter.Split('=', 2);
                if (pair.Length == 2 && pair[0].Trim().Equals("q", StringComparison.OrdinalIgnoreCase)
                    && !double.TryParse(pair[1].Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out quality))
                    quality = 0;
            }

            // Equal specificity (the same range written twice): the lower weight wins, so an explicit
            // refusal is never overridden by a repetition.
            if (specificity > bestSpecificity || quality < bestQuality)
            {
                bestSpecificity = specificity;
                bestQuality = quality;
            }
        }
        return bestSpecificity >= 0 && bestQuality > 0;
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
