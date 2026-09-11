using Jsonata.Net.Native.SystemTextJson;
using redb.Route.Abstractions;

namespace redb.Route.JsonTransform;

/// <summary>Runtime side of <see cref="JsonTransformDefinition"/>: applies the specification and writes the result to the body.</summary>
internal sealed class JsonTransformProcessor : IProcessor
{
    private readonly CompiledJsonTransform _compiled;
    private readonly JsonTransformOutput _output;
    private readonly JsonTransformOptions _options;

    public JsonTransformProcessor(CompiledJsonTransform compiled, JsonTransformOutput output, JsonTransformOptions options)
    {
        _compiled = compiled;
        _output = output;
        _options = options;
    }

    public Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var result = JsonTransformEngine.Run(_compiled, exchange);
        // Whatever the input was, what leaves this step is JSON (or nothing): the content type says so
        // in every branch, so a stale application/xml from the input never travels with a JSON body.
        exchange.In.ContentType = "application/json";
        if (result is null)
        {
            exchange.In.Body = null;
            return Task.CompletedTask;
        }

        // Two statements on purpose: in one conditional expression the string branch would be converted
        // to JsonNode through its implicit string conversion, and the text result would leave as a JsonValue.
        if (_output == JsonTransformOutput.Node)
            exchange.In.Body = JsonataExtensions.ToSystemTextJsonNode(result);
        else
            exchange.In.Body = _options.Indent ? result.ToIndentedString() : result.ToFlatString();
        return Task.CompletedTask;
    }
}
