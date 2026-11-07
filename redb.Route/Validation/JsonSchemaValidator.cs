using System.Text.Json;
using Json.Schema;
using redb.Route.Abstractions;

namespace redb.Route.Validation;

/// <summary>
/// Validates JSON message bodies against a JSON Schema (draft 2020-12 and earlier).
/// Uses the <c>JsonSchema.Net</c> library internally.
/// <para>
/// The validator extracts the body as a string and parses it, or takes a <see cref="JsonDocument"/>
/// / <see cref="JsonElement"/> body directly.
/// </para>
/// </summary>
public sealed class JsonSchemaValidator : IMessageValidator
{
    private readonly JsonSchema _schema;
    private readonly EvaluationOptions _options;

    /// <summary>Creates a validator from a pre-parsed <see cref="JsonSchema"/>.</summary>
    /// <param name="schema">The JSON Schema to validate against.</param>
    public JsonSchemaValidator(JsonSchema schema)
    {
        _schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _options = new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
            RequireFormatValidation = true
        };
    }

    /// <summary>Creates a validator from a JSON Schema string.</summary>
    /// <param name="schemaJson">JSON Schema as a string.</param>
    public JsonSchemaValidator(string schemaJson)
        : this(JsonSchema.FromText(schemaJson ?? throw new ArgumentNullException(nameof(schemaJson))))
    {
    }

    /// <inheritdoc />
    public ValidationResult Validate(IExchange exchange)
    {
        ArgumentNullException.ThrowIfNull(exchange, nameof(exchange));

        JsonDocument doc;
        var body = exchange.In.Body;

        if (body is null)
            return ValidationResult.Failure("Message body is null");

        if (body is JsonDocument jdoc)
        {
            doc = jdoc;
        }
        else if (body is JsonElement jel)
        {
            // Wrap JsonElement into JsonDocument via serialization round-trip
            doc = JsonDocument.Parse(jel.GetRawText());
        }
        else if (body is string str)
        {
            try
            {
                doc = JsonDocument.Parse(str);
            }
            catch (JsonException ex)
            {
                return ValidationResult.Failure($"Invalid JSON: {ex.Message}");
            }
        }
        else
        {
            // Serialize POCO to JSON first
            try
            {
                var json = JsonSerializer.Serialize(body, body.GetType());
                doc = JsonDocument.Parse(json);
            }
            catch (Exception ex)
            {
                return ValidationResult.Failure($"Cannot serialize body to JSON: {ex.Message}");
            }
        }

        var result = _schema.Evaluate(doc, _options);

        if (result.IsValid)
            return ValidationResult.Success();

        var errors = new List<string>();
        if (result.Details != null)
        {
            foreach (var detail in result.Details)
            {
                if (detail.HasErrors && detail.Errors != null)
                {
                    foreach (var error in detail.Errors)
                    {
                        errors.Add($"{detail.InstanceLocation}: {error.Value}");
                    }
                }
            }
        }

        if (errors.Count == 0)
            errors.Add("JSON Schema validation failed");

        return ValidationResult.Failure(errors);
    }
}
