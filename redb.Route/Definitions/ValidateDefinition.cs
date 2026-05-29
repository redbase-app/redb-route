using Json.Schema;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Validation;
using System.Xml.Schema;

namespace redb.Route.Definitions;

/// <summary>
/// Leaf definition that validates the exchange using an <see cref="IMessageValidator"/> instance.
/// </summary>
public sealed class ValidateInstanceDefinition : ProcessorDefinition
{
    private readonly IMessageValidator _validator;
    private readonly bool _throwOnFailure;

    /// <summary>Gets the validator instance.</summary>
    public IMessageValidator Validator => _validator;

    /// <summary>Gets whether a validation failure throws an exception.</summary>
    public bool ThrowOnFailure => _throwOnFailure;

    /// <summary>Creates a validate definition from a validator instance.</summary>
    public ValidateInstanceDefinition(IMessageValidator validator, bool throwOnFailure = true)
    {
        ArgumentNullException.ThrowIfNull(validator);
        _validator = validator;
        _throwOnFailure = throwOnFailure;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new ValidateProcessor(_validator, _throwOnFailure);
}

/// <summary>
/// Leaf definition that validates the exchange using a predicate function.
/// </summary>
public sealed class ValidatePredicateDefinition : ProcessorDefinition
{
    private readonly Func<IExchange, bool> _predicate;
    private readonly string _errorMessage;
    private readonly bool _throwOnFailure;

    /// <summary>Gets the error message used when validation fails.</summary>
    public string ErrorMessage => _errorMessage;

    /// <summary>Gets whether a validation failure throws an exception.</summary>
    public bool ThrowOnFailure => _throwOnFailure;

    /// <summary>Creates a validate definition from a predicate.</summary>
    public ValidatePredicateDefinition(Func<IExchange, bool> predicate, string errorMessage, bool throwOnFailure = true)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);
        _predicate = predicate;
        _errorMessage = errorMessage;
        _throwOnFailure = throwOnFailure;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new ValidateProcessor(new PredicateValidator(_predicate, _errorMessage), _throwOnFailure);
}

/// <summary>
/// Leaf definition that validates the exchange body against a JSON Schema specified as a string.
/// </summary>
public sealed class ValidateJsonSchemaStringDefinition : ProcessorDefinition
{
    private readonly string _schemaJson;
    private readonly bool _throwOnFailure;

    /// <summary>Gets the JSON Schema string.</summary>
    public string SchemaJson => _schemaJson;

    /// <summary>Gets whether a validation failure throws an exception.</summary>
    public bool ThrowOnFailure => _throwOnFailure;

    /// <summary>Creates a validate definition from a JSON Schema string.</summary>
    public ValidateJsonSchemaStringDefinition(string schemaJson, bool throwOnFailure = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaJson);
        _schemaJson = schemaJson;
        _throwOnFailure = throwOnFailure;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new ValidateProcessor(new JsonSchemaValidator(_schemaJson), _throwOnFailure);
}

/// <summary>
/// Leaf definition that validates the exchange body against a <see cref="JsonSchema"/> object.
/// </summary>
public sealed class ValidateJsonSchemaObjectDefinition : ProcessorDefinition
{
    private readonly JsonSchema _schema;
    private readonly bool _throwOnFailure;

    /// <summary>Creates a validate definition from a JsonSchema object.</summary>
    public ValidateJsonSchemaObjectDefinition(JsonSchema schema, bool throwOnFailure = true)
    {
        ArgumentNullException.ThrowIfNull(schema);
        _schema = schema;
        _throwOnFailure = throwOnFailure;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new ValidateProcessor(new JsonSchemaValidator(_schema), _throwOnFailure);
}

/// <summary>
/// Leaf definition that validates the exchange body against an XSD schema string.
/// </summary>
public sealed class ValidateXsdStringDefinition : ProcessorDefinition
{
    private readonly string _xsdContent;
    private readonly bool _throwOnFailure;

    /// <summary>Gets the XSD content string.</summary>
    public string XsdContent => _xsdContent;

    /// <summary>Gets whether a validation failure throws an exception.</summary>
    public bool ThrowOnFailure => _throwOnFailure;

    /// <summary>Creates a validate definition from an XSD content string.</summary>
    public ValidateXsdStringDefinition(string xsdContent, bool throwOnFailure = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xsdContent);
        _xsdContent = xsdContent;
        _throwOnFailure = throwOnFailure;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new ValidateProcessor(new XsdValidator(_xsdContent), _throwOnFailure);
}

/// <summary>
/// Leaf definition that validates the exchange body against an XSD schema with a target namespace.
/// </summary>
public sealed class ValidateXsdNamespaceDefinition : ProcessorDefinition
{
    private readonly string? _targetNamespace;
    private readonly string _xsdContent;
    private readonly bool _throwOnFailure;

    /// <summary>Gets the target XML namespace (may be null).</summary>
    public string? TargetNamespace => _targetNamespace;

    /// <summary>Gets the XSD content string.</summary>
    public string XsdContent => _xsdContent;

    /// <summary>Gets whether a validation failure throws an exception.</summary>
    public bool ThrowOnFailure => _throwOnFailure;

    /// <summary>Creates a validate definition with a namespace-qualified XSD.</summary>
    public ValidateXsdNamespaceDefinition(string? targetNamespace, string xsdContent, bool throwOnFailure = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xsdContent);
        _targetNamespace = targetNamespace;
        _xsdContent = xsdContent;
        _throwOnFailure = throwOnFailure;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new ValidateProcessor(new XsdValidator(_targetNamespace, _xsdContent), _throwOnFailure);
}

/// <summary>
/// Leaf definition that validates the exchange body against a pre-built <see cref="XmlSchemaSet"/>.
/// </summary>
public sealed class ValidateXsdSchemaSetDefinition : ProcessorDefinition
{
    private readonly XmlSchemaSet _schemaSet;
    private readonly bool _throwOnFailure;

    /// <summary>Creates a validate definition from an XmlSchemaSet.</summary>
    public ValidateXsdSchemaSetDefinition(XmlSchemaSet schemaSet, bool throwOnFailure = true)
    {
        ArgumentNullException.ThrowIfNull(schemaSet);
        _schemaSet = schemaSet;
        _throwOnFailure = throwOnFailure;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new ValidateProcessor(new XsdValidator(_schemaSet), _throwOnFailure);
}
