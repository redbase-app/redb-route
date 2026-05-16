using System.Collections.Concurrent;
using System.Xml;
using System.Xml.Schema;
using Json.Schema;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Validation;

// ── Component ────────────────────────────────────────────────────────────────

/// <summary>
/// Component for schema-based message validation.
/// <para>
/// URI format: <c>validator:path/to/schema.json</c> or <c>validator:path/to/schema.xsd</c>.
/// The schema format is auto-detected from the file extension (<c>.xsd</c> → XSD, otherwise JSON Schema).
/// </para>
/// <para>
/// Parameters:
/// <list type="bullet">
///   <item><c>format</c> — Force format: <c>json</c> or <c>xml</c> (overrides auto-detection).</item>
///   <item><c>throwOnFailure</c> — Whether to throw on failure (<c>true</c> by default).</item>
///   <item><c>targetNamespace</c> — Target namespace for XSD schemas (optional).</item>
/// </list>
/// </para>
/// </summary>
public sealed class ValidatorComponent : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "validator";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var options = new ValidatorEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.SchemaPath = uri.Path;
        options.Validate();
        return new ValidatorEndpoint(uri, this, options);
    }
}

// ── Options ──────────────────────────────────────────────────────────────────

/// <summary>Options for the validator endpoint.</summary>
public sealed class ValidatorEndpointOptions : EndpointOptions
{
    /// <summary>Path to the schema file (JSON Schema or XSD).</summary>
    public string SchemaPath { get; set; } = string.Empty;

    /// <summary>
    /// Explicit format override: <c>json</c> or <c>xml</c>. If not set, auto-detected from file extension.
    /// </summary>
    public string? Format { get; set; }

    /// <summary>Whether to throw on validation failure. Default: <c>true</c>.</summary>
    public bool ThrowOnFailure { get; set; } = true;

    /// <summary>Target namespace for XSD schemas (optional).</summary>
    public string? TargetNamespace { get; set; }

    /// <inheritdoc />
    public override void Validate()
    {
        if (string.IsNullOrWhiteSpace(SchemaPath))
            throw new ArgumentException("Schema path is required for the validator component.");

        if (!File.Exists(SchemaPath))
            throw new FileNotFoundException($"Schema file not found: {SchemaPath}");

        if (Format != null &&
            !Format.Equals("json", StringComparison.OrdinalIgnoreCase) &&
            !Format.Equals("xml", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Invalid format '{Format}'. Supported: json, xml.");
        }
    }
}

// ── Endpoint ─────────────────────────────────────────────────────────────────

/// <summary>Endpoint that performs schema validation (JSON Schema or XSD).</summary>
public sealed class ValidatorEndpoint : EndpointBase<ValidatorEndpointOptions>
{
    private readonly IMessageValidator _validator;
    private readonly bool _throwOnFailure;

    internal ValidatorEndpoint(EndpointUri uri, ValidatorComponent component, ValidatorEndpointOptions options)
        : base(uri, component, options)
    {
        _throwOnFailure = options.ThrowOnFailure;
        _validator = BuildValidator(options);
    }

    /// <inheritdoc />
    public override IProducer CreateProducer() => new ValidatorProducer(this);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
        => throw new NotSupportedException("Validator component does not support consuming (from). Use .To(\"validator:...\") instead.");

    internal IMessageValidator Validator => _validator;
    internal bool ThrowOnFailure => _throwOnFailure;

    private static IMessageValidator BuildValidator(ValidatorEndpointOptions options)
    {
        var isXsd = IsXsdSchema(options);
        var schemaContent = File.ReadAllText(options.SchemaPath);

        if (isXsd)
        {
            return string.IsNullOrEmpty(options.TargetNamespace)
                ? new XsdValidator(schemaContent)
                : new XsdValidator(options.TargetNamespace, schemaContent);
        }

        return new JsonSchemaValidator(schemaContent);
    }

    private static bool IsXsdSchema(ValidatorEndpointOptions options)
    {
        if (options.Format != null)
            return options.Format.Equals("xml", StringComparison.OrdinalIgnoreCase);

        // Auto-detect from file extension
        var ext = Path.GetExtension(options.SchemaPath);
        return ext.Equals(".xsd", StringComparison.OrdinalIgnoreCase);
    }
}

// ── Producer ─────────────────────────────────────────────────────────────────

/// <summary>Producer that triggers validation when the route sends to this endpoint.</summary>
public sealed class ValidatorProducer : IProducer
{
    private readonly ValidatorEndpoint _endpoint;

    internal ValidatorProducer(ValidatorEndpoint endpoint)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    /// <inheritdoc />
    public Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var result = _endpoint.Validator.Validate(exchange);

        exchange.Properties[ValidateProcessor.ValidationResultProperty] = result.IsValid;
        exchange.Properties[ValidateProcessor.ValidationErrorsProperty] = result.IsValid
            ? null
            : string.Join("; ", result.Errors);

        if (!result.IsValid && _endpoint.ThrowOnFailure)
            throw new ValidationException(result.Errors);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Start(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task Stop(CancellationToken ct = default) => Task.CompletedTask;
}
