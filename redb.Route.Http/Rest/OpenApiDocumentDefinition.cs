using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using redb.Route.Abstractions;
using redb.Route.Definitions;
using redb.Route.Processors;

namespace redb.Route.Http.Rest;

/// <summary>
/// Serves the OpenAPI 3.0.3 document of a <see cref="RestDefinition"/>: paths and operations from the
/// verbs, path parameters from the templates, request / response schemas from <c>Type&lt;T&gt;()</c> /
/// <c>OutType&lt;T&gt;()</c> (camelCase properties, shared types under <c>components/schemas</c>).
/// Built on first request from the declarations as they are at that moment.
/// </summary>
public sealed class OpenApiDocumentDefinition : ProcessorDefinition
{
    private readonly RestDefinition _rest;
    private readonly Lazy<byte[]> _document;

    internal OpenApiDocumentDefinition(RestDefinition rest)
    {
        _rest = rest;
        _document = new Lazy<byte[]>(() => JsonSerializer.SerializeToUtf8Bytes(Build(_rest), new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new DelegateProcessor(exchange =>
        {
            exchange.In.Body = _document.Value;
            exchange.In.ContentType = "application/json";
            exchange.In.Headers[HttpHeaders.ResponseContentType] = "application/json";
        });

    /// <summary>Builds the document as a JSON tree (also used by tests and tooling).</summary>
    public static JsonObject Build(RestDefinition rest)
    {
        var schemas = new JsonObject();
        var paths = new JsonObject();

        foreach (var group in rest.Verbs.GroupBy(v => v.FullPath))
        {
            var item = new JsonObject();
            foreach (var verb in group)
                item[verb.Method.ToLowerInvariant()] = Operation(verb, rest.Options, schemas);
            paths[group.Key] = item;
        }

        return new JsonObject
        {
            ["openapi"] = "3.0.3",
            ["info"] = new JsonObject { ["title"] = rest.Options.Title, ["version"] = rest.Options.Version },
            ["paths"] = paths,
            ["components"] = new JsonObject { ["schemas"] = schemas },
        };
    }

    private static JsonObject Operation(RestVerbDefinition verb, RestOptions options, JsonObject schemas)
    {
        var operation = new JsonObject
        {
            ["operationId"] = verb.RouteId ?? $"{verb.Method.ToLowerInvariant()}{verb.FullPath.Replace("/", "_").Replace("{", "").Replace("}", "")}",
        };
        if (verb.Summary is not null) operation["summary"] = verb.Summary;

        var parameters = new JsonArray();
        foreach (var name in verb.PathParameters)
            parameters.Add(new JsonObject
            {
                ["name"] = name, ["in"] = "path", ["required"] = true,
                ["schema"] = new JsonObject { ["type"] = "string" },
            });
        if (parameters.Count > 0) operation["parameters"] = parameters;

        var consumes = verb.EffectiveConsumes(options);
        if (verb.RequestType is not null || consumes is not null)
        {
            var content = new JsonObject();
            var media = new JsonObject();
            if (verb.RequestType is not null) media["schema"] = SchemaRef(verb.RequestType, schemas);
            content[consumes ?? "application/json"] = media;
            operation["requestBody"] = new JsonObject { ["required"] = true, ["content"] = content };
        }

        var ok = new JsonObject { ["description"] = "OK" };
        var produces = verb.EffectiveProduces(options);
        if (verb.ResponseType is not null || produces is not null)
        {
            var media = new JsonObject();
            if (verb.ResponseType is not null) media["schema"] = SchemaRef(verb.ResponseType, schemas);
            ok["content"] = new JsonObject { [produces ?? "application/json"] = media };
        }
        operation["responses"] = new JsonObject { ["200"] = ok };
        return operation;
    }

    private static JsonNode SchemaRef(Type type, JsonObject schemas)
    {
        var inline = Primitive(type);
        if (inline is not null) return inline;

        if (type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type))
        {
            var element = type.IsArray ? type.GetElementType()! : type.GetGenericArguments().FirstOrDefault() ?? typeof(object);
            return new JsonObject { ["type"] = "array", ["items"] = SchemaRef(element, schemas) };
        }

        var name = SchemaName(type, schemas);
        if (!schemas.ContainsKey(name))
        {
            var properties = new JsonObject();
            // Registered first, so cycles resolve to the $ref; x-clr-type lets a later type with the same short name get its own component.
            schemas[name] = new JsonObject { ["type"] = "object", ["properties"] = properties, ["x-clr-type"] = type.FullName ?? type.Name };
            foreach (var property in type.GetProperties().Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
                properties[JsonNamingPolicy.CamelCase.ConvertName(property.Name)] = SchemaRef(property.PropertyType, schemas);
        }
        return new JsonObject { ["$ref"] = $"#/components/schemas/{name}" };
    }

    /// <summary>
    /// Component name for a type: its short name, unless another type already holds it, then the
    /// namespace-qualified name, then a numbered one (<c>V1.Order</c> and <c>V2.Order</c> in one
    /// document must not share a schema).
    /// </summary>
    private static string SchemaName(Type type, JsonObject schemas)
    {
        var full = type.FullName ?? type.Name;
        var shortName = type.Name;
        var backtick = shortName.IndexOf('`');
        if (backtick > 0) shortName = shortName[..backtick];
        var qualified = new string(full.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '.' ? c : '_').ToArray());

        foreach (var candidate in new[] { shortName, qualified })
            if (IsFreeOrOwn(candidate)) return candidate;
        for (var i = 2; ; i++)
            if (IsFreeOrOwn($"{shortName}_{i}")) return $"{shortName}_{i}";

        bool IsFreeOrOwn(string candidate)
            => schemas[candidate] is not JsonObject existing || existing["x-clr-type"]?.GetValue<string>() == full;
    }

    private static JsonObject? Primitive(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string) || type == typeof(char) || type == typeof(Guid)) return new JsonObject { ["type"] = "string" };
        if (type == typeof(bool)) return new JsonObject { ["type"] = "boolean" };
        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)) return new JsonObject { ["type"] = "integer" };
        if (type == typeof(decimal) || type == typeof(double) || type == typeof(float)) return new JsonObject { ["type"] = "number" };
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) return new JsonObject { ["type"] = "string", ["format"] = "date-time" };
        if (type == typeof(object)) return new JsonObject();
        if (type.IsEnum) return new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Enum.GetNames(type).Select(n => (JsonNode)n).ToArray()) };
        return null;
    }
}
