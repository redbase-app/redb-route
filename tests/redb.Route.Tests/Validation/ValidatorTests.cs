using System.Text.Json;
using System.Xml.Linq;
using System.Xml.Schema;
using FluentAssertions;
using Json.Schema;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Validation;
using Xunit;

namespace redb.Route.Tests.Validation;

public class ValidatorTests
{
    // ═══════════════════════════════════════════════════════════════════
    //  Sample data
    // ═══════════════════════════════════════════════════════════════════

    private const string PersonJsonSchema = """
    {
        "type": "object",
        "properties": {
            "name": { "type": "string" },
            "age": { "type": "integer", "minimum": 0 }
        },
        "required": ["name", "age"]
    }
    """;

    private const string ValidPersonJson = """{"name":"John","age":30}""";
    private const string InvalidPersonJson_MissingAge = """{"name":"John"}""";
    private const string InvalidPersonJson_WrongType = """{"name":"John","age":"thirty"}""";
    private const string NotJson = "this is not json";

    private const string PersonXsd = """
    <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
        <xs:element name="person">
            <xs:complexType>
                <xs:sequence>
                    <xs:element name="name" type="xs:string"/>
                    <xs:element name="age" type="xs:integer"/>
                </xs:sequence>
            </xs:complexType>
        </xs:element>
    </xs:schema>
    """;

    private const string ValidPersonXml = "<person><name>John</name><age>30</age></person>";
    private const string InvalidPersonXml_MissingAge = "<person><name>John</name></person>";
    private const string InvalidPersonXml_WrongType = "<person><name>John</name><age>thirty</age></person>";
    private const string NotXml = "this is not xml";

    private static IExchange CreateExchange(object? body = null)
        => new Exchange(new Message(body));

    // ═══════════════════════════════════════════════════════════════════
    //  JsonSchemaValidator
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void JsonSchemaValidator_ValidJson_ReturnsSuccess()
    {
        var validator = new JsonSchemaValidator(PersonJsonSchema);
        var exchange = CreateExchange(ValidPersonJson);

        var result = validator.Validate(exchange);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void JsonSchemaValidator_MissingRequiredField_ReturnsFailure()
    {
        var validator = new JsonSchemaValidator(PersonJsonSchema);
        var exchange = CreateExchange(InvalidPersonJson_MissingAge);

        var result = validator.Validate(exchange);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void JsonSchemaValidator_WrongType_ReturnsFailure()
    {
        var validator = new JsonSchemaValidator(PersonJsonSchema);
        var exchange = CreateExchange(InvalidPersonJson_WrongType);

        var result = validator.Validate(exchange);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void JsonSchemaValidator_NotJson_ReturnsFailure()
    {
        var validator = new JsonSchemaValidator(PersonJsonSchema);
        var exchange = CreateExchange(NotJson);

        var result = validator.Validate(exchange);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Invalid JSON"));
    }

    [Fact]
    public void JsonSchemaValidator_NullBody_ReturnsFailure()
    {
        var validator = new JsonSchemaValidator(PersonJsonSchema);
        var exchange = CreateExchange(null);

        var result = validator.Validate(exchange);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain("Message body is null");
    }

    [Fact]
    public void JsonSchemaValidator_JsonDocumentBody_ValidatesDirectly()
    {
        var validator = new JsonSchemaValidator(PersonJsonSchema);
        var doc = JsonDocument.Parse(ValidPersonJson);
        var exchange = CreateExchange(doc);

        var result = validator.Validate(exchange);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void JsonSchemaValidator_PocoBody_SerializesAndValidates()
    {
        var validator = new JsonSchemaValidator(PersonJsonSchema);
        var exchange = CreateExchange(new { name = "John", age = 30 });

        var result = validator.Validate(exchange);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void JsonSchemaValidator_PocoBody_Invalid_ReturnsFailure()
    {
        var validator = new JsonSchemaValidator(PersonJsonSchema);
        var exchange = CreateExchange(new { name = "John" }); // missing age

        var result = validator.Validate(exchange);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void JsonSchemaValidator_FromSchemaObject_Works()
    {
        var schema = JsonSchema.FromText(PersonJsonSchema);
        var validator = new JsonSchemaValidator(schema);
        var exchange = CreateExchange(ValidPersonJson);

        var result = validator.Validate(exchange);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void JsonSchemaValidator_NullSchema_Throws()
    {
        var act = () => new JsonSchemaValidator((JsonSchema)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void JsonSchemaValidator_NullSchemaString_Throws()
    {
        var act = () => new JsonSchemaValidator((string)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  XsdValidator
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void XsdValidator_ValidXml_ReturnsSuccess()
    {
        var validator = new XsdValidator(PersonXsd);
        var exchange = CreateExchange(ValidPersonXml);

        var result = validator.Validate(exchange);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void XsdValidator_MissingRequiredElement_ReturnsFailure()
    {
        var validator = new XsdValidator(PersonXsd);
        var exchange = CreateExchange(InvalidPersonXml_MissingAge);

        var result = validator.Validate(exchange);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void XsdValidator_WrongType_ReturnsFailure()
    {
        var validator = new XsdValidator(PersonXsd);
        var exchange = CreateExchange(InvalidPersonXml_WrongType);

        var result = validator.Validate(exchange);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void XsdValidator_NotXml_ReturnsFailure()
    {
        var validator = new XsdValidator(PersonXsd);
        var exchange = CreateExchange(NotXml);

        var result = validator.Validate(exchange);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void XsdValidator_NullBody_ReturnsFailure()
    {
        var validator = new XsdValidator(PersonXsd);
        var exchange = CreateExchange(null);

        var result = validator.Validate(exchange);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain("Message body is null");
    }

    [Fact]
    public void XsdValidator_XDocumentBody_Validates()
    {
        var validator = new XsdValidator(PersonXsd);
        var doc = XDocument.Parse(ValidPersonXml);
        var exchange = CreateExchange(doc);

        var result = validator.Validate(exchange);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void XsdValidator_XElementBody_Validates()
    {
        var validator = new XsdValidator(PersonXsd);
        var el = XElement.Parse(ValidPersonXml);
        var exchange = CreateExchange(el);

        var result = validator.Validate(exchange);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void XsdValidator_FromSchemaSet_Works()
    {
        var schemaSet = new XmlSchemaSet();
        using var reader = new StringReader(PersonXsd);
        schemaSet.Add("", System.Xml.XmlReader.Create(reader));
        schemaSet.Compile();

        var validator = new XsdValidator(schemaSet);
        var exchange = CreateExchange(ValidPersonXml);

        var result = validator.Validate(exchange);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void XsdValidator_WithTargetNamespace_Works()
    {
        const string nsXsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"
                   targetNamespace="http://example.com/person"
                   xmlns:tns="http://example.com/person"
                   elementFormDefault="qualified">
            <xs:element name="person">
                <xs:complexType>
                    <xs:sequence>
                        <xs:element name="name" type="xs:string"/>
                    </xs:sequence>
                </xs:complexType>
            </xs:element>
        </xs:schema>
        """;

        const string nsXml = """<person xmlns="http://example.com/person"><name>John</name></person>""";

        var validator = new XsdValidator("http://example.com/person", nsXsd);
        var exchange = CreateExchange(nsXml);

        var result = validator.Validate(exchange);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void XsdValidator_NullSchemaSet_Throws()
    {
        var act = () => new XsdValidator((XmlSchemaSet)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void XsdValidator_NullXsdString_Throws()
    {
        var act = () => new XsdValidator((string)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PredicateValidator
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void PredicateValidator_PredicateTrue_ReturnsSuccess()
    {
        var validator = new PredicateValidator(e => e.In.Body != null);
        var exchange = CreateExchange("data");

        var result = validator.Validate(exchange);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void PredicateValidator_PredicateFalse_ReturnsFailure()
    {
        var validator = new PredicateValidator(e => e.In.Body != null, "Body must not be null");
        var exchange = CreateExchange(null);

        var result = validator.Validate(exchange);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain("Body must not be null");
    }

    [Fact]
    public void PredicateValidator_DynamicErrorMessage_UsesFactory()
    {
        var validator = new PredicateValidator(
            e => e.In.Body is string s && s.Length > 5,
            e => $"Body too short: {((string?)e.In.Body)?.Length ?? 0} chars");

        var exchange = CreateExchange("abc");

        var result = validator.Validate(exchange);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("3 chars"));
    }

    [Fact]
    public void PredicateValidator_NullPredicate_Throws()
    {
        var act = () => new PredicateValidator(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ValidateProcessor
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ValidateProcessor_Valid_SetsProperties()
    {
        var validator = new JsonSchemaValidator(PersonJsonSchema);
        var processor = new ValidateProcessor(validator);
        var exchange = CreateExchange(ValidPersonJson);

        await processor.Process(exchange);

        exchange.Properties[ValidateProcessor.ValidationResultProperty].Should().Be(true);
        exchange.Properties[ValidateProcessor.ValidationErrorsProperty].Should().BeNull();
    }

    [Fact]
    public async Task ValidateProcessor_Invalid_ThrowsByDefault()
    {
        var validator = new JsonSchemaValidator(PersonJsonSchema);
        var processor = new ValidateProcessor(validator);
        var exchange = CreateExchange(InvalidPersonJson_MissingAge);

        var act = () => processor.Process(exchange);

        await act.Should().ThrowAsync<ValidationException>();
        exchange.Properties[ValidateProcessor.ValidationResultProperty].Should().Be(false);
        exchange.Properties[ValidateProcessor.ValidationErrorsProperty].Should().NotBeNull();
    }

    [Fact]
    public async Task ValidateProcessor_Invalid_SoftMode_DoesNotThrow()
    {
        var validator = new JsonSchemaValidator(PersonJsonSchema);
        var processor = new ValidateProcessor(validator, throwOnFailure: false);
        var exchange = CreateExchange(InvalidPersonJson_MissingAge);

        await processor.Process(exchange);

        exchange.Properties[ValidateProcessor.ValidationResultProperty].Should().Be(false);
        var errors = (string?)exchange.Properties[ValidateProcessor.ValidationErrorsProperty];
        errors.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ValidateProcessor_NullValidator_Throws()
    {
        var act = () => new ValidateProcessor(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ValidationException
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ValidationException_ContainsErrors()
    {
        var errors = new List<string> { "err1", "err2" };
        var ex = new ValidationException(errors);

        ex.Errors.Should().BeEquivalentTo(new[] { "err1", "err2" });
        ex.Message.Should().Contain("err1");
        ex.Message.Should().Contain("err2");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ValidationResult
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ValidationResult_Success_IsValid()
    {
        var result = ValidationResult.Success();

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidationResult_Failure_SingleError()
    {
        var result = ValidationResult.Failure("bad");

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Be("bad");
    }

    [Fact]
    public void ValidationResult_Failure_MultipleErrors()
    {
        var result = ValidationResult.Failure(new[] { "err1", "err2" });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(2);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  DSL Integration — .Validate() step
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void DSL_Validate_WithInstance_RecordsStep()
    {
        var def = new RouteDefinition();
        var validator = new JsonSchemaValidator(PersonJsonSchema);

        def.From("direct://input")
           .Validate(validator)
           .To("direct://output");

        def.Steps.Should().ContainSingle(s => s is ValidateInstanceStep);
    }

    [Fact]
    public void DSL_Validate_WithPredicate_RecordsStep()
    {
        var def = new RouteDefinition();

        def.From("direct://input")
           .Validate(e => e.In.Body != null, "Body required")
           .To("direct://output");

        def.Steps.Should().ContainSingle(s => s is ValidatePredicateStep);
    }

    [Fact]
    public void DSL_Validate_WithJsonSchemaValidator_RecordsCorrectStep()
    {
        var def = new RouteDefinition();
        var validator = new JsonSchemaValidator(PersonJsonSchema);

        def.From("direct://input")
           .Validate(validator)
           .To("mock://result");

        var step = def.Steps.OfType<ValidateInstanceStep>().Single();
        step.Validator.Should().BeSameAs(validator);
        step.ThrowOnFailure.Should().BeTrue();
    }

    [Fact]
    public void DSL_Validate_WithXsdValidator_RecordsCorrectStep()
    {
        var def = new RouteDefinition();
        var validator = new XsdValidator(PersonXsd);

        def.From("direct://input")
           .Validate(validator)
           .To("mock://result");

        var step = def.Steps.OfType<ValidateInstanceStep>().Single();
        step.Validator.Should().BeSameAs(validator);
    }

    [Fact]
    public void DSL_Validate_WithPredicate_RecordsCorrectStep()
    {
        var def = new RouteDefinition();

        def.From("direct://input")
           .Validate(e => e.In.Body != null, "Body required")
           .To("mock://result");

        var step = def.Steps.OfType<ValidatePredicateStep>().Single();
        step.ErrorMessage.Should().Be("Body required");
        step.ThrowOnFailure.Should().BeTrue();
    }

    [Fact]
    public void DSL_Validate_SoftMode_NoThrow()
    {
        var def = new RouteDefinition();
        var validator = new JsonSchemaValidator(PersonJsonSchema);

        def.From("direct://input")
           .Validate(validator, throwOnFailure: false)
           .To("mock://result");

        def.Steps.OfType<ValidateInstanceStep>().Single().ThrowOnFailure.Should().BeFalse();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  End-to-end pipeline validation
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Pipeline_JsonValidation_Valid_PassesThrough()
    {
        var validator = new JsonSchemaValidator(PersonJsonSchema);
        var processor = new ValidateProcessor(validator);
        var exchange = CreateExchange(ValidPersonJson);

        await processor.Process(exchange);

        // No exception, body untouched
        exchange.In.Body.Should().Be(ValidPersonJson);
        ((bool)exchange.Properties[ValidateProcessor.ValidationResultProperty]!).Should().BeTrue();
    }

    [Fact]
    public async Task Pipeline_XsdValidation_Valid_PassesThrough()
    {
        var validator = new XsdValidator(PersonXsd);
        var processor = new ValidateProcessor(validator);
        var exchange = CreateExchange(ValidPersonXml);

        await processor.Process(exchange);

        exchange.In.Body.Should().Be(ValidPersonXml);
        ((bool)exchange.Properties[ValidateProcessor.ValidationResultProperty]!).Should().BeTrue();
    }

    [Fact]
    public async Task Pipeline_JsonValidation_Invalid_Throws()
    {
        var validator = new JsonSchemaValidator(PersonJsonSchema);
        var processor = new ValidateProcessor(validator);
        var exchange = CreateExchange(InvalidPersonJson_MissingAge);

        var act = () => processor.Process(exchange);

        var ex = (await act.Should().ThrowAsync<ValidationException>()).Which;
        ex.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Pipeline_XsdValidation_Invalid_Throws()
    {
        var validator = new XsdValidator(PersonXsd);
        var processor = new ValidateProcessor(validator);
        var exchange = CreateExchange(InvalidPersonXml_MissingAge);

        var act = () => processor.Process(exchange);

        var ex = (await act.Should().ThrowAsync<ValidationException>()).Which;
        ex.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Pipeline_SoftValidation_Invalid_SetsPropertyOnly()
    {
        var validator = new XsdValidator(PersonXsd);
        var processor = new ValidateProcessor(validator, throwOnFailure: false);
        var exchange = CreateExchange(InvalidPersonXml_MissingAge);

        await processor.Process(exchange);

        ((bool)exchange.Properties[ValidateProcessor.ValidationResultProperty]!).Should().BeFalse();
        var errors = (string?)exchange.Properties[ValidateProcessor.ValidationErrorsProperty];
        errors.Should().NotBeNullOrEmpty();
        // Body untouched
        exchange.In.Body.Should().Be(InvalidPersonXml_MissingAge);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Mix: validate JSON then transform, validate XML then transform
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Pipeline_ValidateJson_ThenTransform()
    {
        var validator = new JsonSchemaValidator(PersonJsonSchema);
        var exchange = CreateExchange(ValidPersonJson);

        // Validate
        var proc = new ValidateProcessor(validator);
        await proc.Process(exchange);

        // Transform after valid
        exchange.In.Body = $"Validated: {exchange.In.Body}";
        exchange.In.Body.Should().Be("Validated: {\"name\":\"John\",\"age\":30}");
    }

    [Fact]
    public async Task Pipeline_ValidateXsd_ThenTransform()
    {
        var validator = new XsdValidator(PersonXsd);
        var exchange = CreateExchange(ValidPersonXml);

        var proc = new ValidateProcessor(validator);
        await proc.Process(exchange);

        exchange.In.Body = $"Validated: {exchange.In.Body}";
        ((string)exchange.In.Body!).Should().StartWith("Validated:");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ValidatorComponent (URI-based)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ValidatorComponent_Scheme_IsValidator()
    {
        var component = new ValidatorComponent();
        component.Scheme.Should().Be("validator");
    }

    [Fact]
    public void ValidatorComponent_JsonSchema_CreatesEndpoint()
    {
        var component = new ValidatorComponent();
        var schemaFile = CreateTempFile(PersonJsonSchema, ".json");

        try
        {
            var uri = new EndpointUri("validator", schemaFile,
                $"validator://{schemaFile}",
                new Dictionary<string, string>());

            var endpoint = component.CreateEndpoint(uri);
            endpoint.Should().NotBeNull();
            endpoint.Should().BeOfType<ValidatorEndpoint>();
        }
        finally
        {
            File.Delete(schemaFile);
        }
    }

    [Fact]
    public void ValidatorComponent_XsdSchema_CreatesEndpoint()
    {
        var component = new ValidatorComponent();
        var schemaFile = CreateTempFile(PersonXsd, ".xsd");

        try
        {
            var uri = new EndpointUri("validator", schemaFile,
                $"validator://{schemaFile}",
                new Dictionary<string, string>());

            var endpoint = component.CreateEndpoint(uri);
            endpoint.Should().NotBeNull();
        }
        finally
        {
            File.Delete(schemaFile);
        }
    }

    [Fact]
    public void ValidatorComponent_MissingFile_Throws()
    {
        var component = new ValidatorComponent();
        var uri = new EndpointUri("validator", "nonexistent.json",
            "validator://nonexistent.json",
            new Dictionary<string, string>());

        var act = () => component.CreateEndpoint(uri);
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void ValidatorComponent_FormatOverride_ForcesJson()
    {
        var component = new ValidatorComponent();
        var schemaFile = CreateTempFile(PersonJsonSchema, ".txt"); // .txt extension, but format=json

        try
        {
            var uri = new EndpointUri("validator", schemaFile,
                $"validator://{schemaFile}?format=json",
                new Dictionary<string, string> { { "format", "json" } });

            var endpoint = component.CreateEndpoint(uri);
            endpoint.Should().NotBeNull();
        }
        finally
        {
            File.Delete(schemaFile);
        }
    }

    [Fact]
    public async Task ValidatorComponent_ProducerValidatesJson()
    {
        var component = new ValidatorComponent();
        var schemaFile = CreateTempFile(PersonJsonSchema, ".json");

        try
        {
            var uri = new EndpointUri("validator", schemaFile,
                $"validator://{schemaFile}",
                new Dictionary<string, string>());

            var endpoint = component.CreateEndpoint(uri);
            var producer = endpoint.CreateProducer();

            var exchange = CreateExchange(ValidPersonJson);
            await producer.Process(exchange);

            ((bool)exchange.Properties[ValidateProcessor.ValidationResultProperty]!).Should().BeTrue();
        }
        finally
        {
            File.Delete(schemaFile);
        }
    }

    [Fact]
    public async Task ValidatorComponent_ProducerThrowsOnInvalidJson()
    {
        var component = new ValidatorComponent();
        var schemaFile = CreateTempFile(PersonJsonSchema, ".json");

        try
        {
            var uri = new EndpointUri("validator", schemaFile,
                $"validator://{schemaFile}",
                new Dictionary<string, string>());

            var endpoint = component.CreateEndpoint(uri);
            var producer = endpoint.CreateProducer();

            var exchange = CreateExchange(InvalidPersonJson_MissingAge);
            var act = () => producer.Process(exchange);

            await act.Should().ThrowAsync<ValidationException>();
        }
        finally
        {
            File.Delete(schemaFile);
        }
    }

    [Fact]
    public async Task ValidatorComponent_SoftMode_DoesNotThrow()
    {
        var component = new ValidatorComponent();
        var schemaFile = CreateTempFile(PersonJsonSchema, ".json");

        try
        {
            var uri = new EndpointUri("validator", schemaFile,
                $"validator://{schemaFile}?throwOnFailure=false",
                new Dictionary<string, string> { { "throwOnFailure", "false" } });

            var endpoint = component.CreateEndpoint(uri);
            var producer = endpoint.CreateProducer();

            var exchange = CreateExchange(InvalidPersonJson_MissingAge);
            await producer.Process(exchange);

            ((bool)exchange.Properties[ValidateProcessor.ValidationResultProperty]!).Should().BeFalse();
        }
        finally
        {
            File.Delete(schemaFile);
        }
    }

    [Fact]
    public void ValidatorComponent_ConsumerNotSupported()
    {
        var component = new ValidatorComponent();
        var schemaFile = CreateTempFile(PersonJsonSchema, ".json");

        try
        {
            var uri = new EndpointUri("validator", schemaFile,
                $"validator://{schemaFile}",
                new Dictionary<string, string>());

            var endpoint = component.CreateEndpoint(uri);
            var act = () => endpoint.CreateConsumer(null!);

            act.Should().Throw<NotSupportedException>();
        }
        finally
        {
            File.Delete(schemaFile);
        }
    }

    [Fact]
    public void ValidatorComponent_InvalidFormatParameter_Throws()
    {
        var component = new ValidatorComponent();
        var schemaFile = CreateTempFile(PersonJsonSchema, ".json");

        try
        {
            var uri = new EndpointUri("validator", schemaFile,
                $"validator://{schemaFile}?format=yaml",
                new Dictionary<string, string> { { "format", "yaml" } });

            var act = () => component.CreateEndpoint(uri);
            act.Should().Throw<ArgumentException>().WithMessage("*yaml*");
        }
        finally
        {
            File.Delete(schemaFile);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  DSL: Explicit format-forcing methods
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ValidateJsonSchema_String_Records_Step()
    {
        var def = new RouteDefinition();
        def.From("direct:start");

        def.ValidateJsonSchema(PersonJsonSchema);

        def.Steps.Should().ContainSingle(s => s is ValidateJsonSchemaStringStep)
            .Which.Should().BeOfType<ValidateJsonSchemaStringStep>()
            .Which.SchemaJson.Should().Be(PersonJsonSchema);
    }

    [Fact]
    public void ValidateJsonSchema_String_ThrowOnFailure_Default_True()
    {
        var def = new RouteDefinition();
        def.From("direct:start");

        def.ValidateJsonSchema(PersonJsonSchema);

        def.Steps.OfType<ValidateJsonSchemaStringStep>().Single()
            .ThrowOnFailure.Should().BeTrue();
    }

    [Fact]
    public void ValidateJsonSchema_String_SoftMode()
    {
        var def = new RouteDefinition();
        def.From("direct:start");

        def.ValidateJsonSchema(PersonJsonSchema, throwOnFailure: false);

        def.Steps.OfType<ValidateJsonSchemaStringStep>().Single()
            .ThrowOnFailure.Should().BeFalse();
    }

    [Fact]
    public void ValidateJsonSchema_Object_Records_Step()
    {
        var schema = JsonSchema.FromText(PersonJsonSchema);
        var def = new RouteDefinition();
        def.From("direct:start");

        def.ValidateJsonSchema(schema);

        def.Steps.Should().ContainSingle(s => s is ValidateJsonSchemaObjectStep)
            .Which.Should().BeOfType<ValidateJsonSchemaObjectStep>();
    }

    [Fact]
    public void ValidateJsonSchema_NullString_Throws()
    {
        var def = new RouteDefinition();
        def.From("direct:start");

        var act = () => def.ValidateJsonSchema((string)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ValidateJsonSchema_NullObject_Throws()
    {
        var def = new RouteDefinition();
        def.From("direct:start");

        var act = () => def.ValidateJsonSchema((JsonSchema)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ValidateXsd_String_Records_Step()
    {
        var def = new RouteDefinition();
        def.From("direct:start");

        def.ValidateXsd(PersonXsd);

        def.Steps.Should().ContainSingle(s => s is ValidateXsdStringStep)
            .Which.Should().BeOfType<ValidateXsdStringStep>()
            .Which.XsdContent.Should().Be(PersonXsd);
    }

    [Fact]
    public void ValidateXsd_String_ThrowOnFailure_Default_True()
    {
        var def = new RouteDefinition();
        def.From("direct:start");

        def.ValidateXsd(PersonXsd);

        def.Steps.OfType<ValidateXsdStringStep>().Single()
            .ThrowOnFailure.Should().BeTrue();
    }

    [Fact]
    public void ValidateXsd_String_SoftMode()
    {
        var def = new RouteDefinition();
        def.From("direct:start");

        def.ValidateXsd(PersonXsd, throwOnFailure: false);

        def.Steps.OfType<ValidateXsdStringStep>().Single()
            .ThrowOnFailure.Should().BeFalse();
    }

    [Fact]
    public void ValidateXsd_Namespace_Records_Step()
    {
        var def = new RouteDefinition();
        def.From("direct:start");

        def.ValidateXsd("http://example.com", PersonXsd);

        def.Steps.Should().ContainSingle(s => s is ValidateXsdNamespaceStep)
            .Which.Should().BeOfType<ValidateXsdNamespaceStep>();
        var step = def.Steps.OfType<ValidateXsdNamespaceStep>().Single();
        step.TargetNamespace.Should().Be("http://example.com");
        step.XsdContent.Should().Be(PersonXsd);
    }

    [Fact]
    public void ValidateXsd_SchemaSet_Records_Step()
    {
        var schemaSet = new XmlSchemaSet();
        using var reader = new StringReader(PersonXsd);
        schemaSet.Add("", System.Xml.XmlReader.Create(reader));
        schemaSet.Compile();

        var def = new RouteDefinition();
        def.From("direct:start");

        def.ValidateXsd(schemaSet);

        def.Steps.Should().ContainSingle(s => s is ValidateXsdSchemaSetStep)
            .Which.Should().BeOfType<ValidateXsdSchemaSetStep>();
    }

    [Fact]
    public void ValidateXsd_NullString_Throws()
    {
        var def = new RouteDefinition();
        def.From("direct:start");

        var act = () => def.ValidateXsd((string)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ValidateXsd_NullSchemaSet_Throws()
    {
        var def = new RouteDefinition();
        def.From("direct:start");

        var act = () => def.ValidateXsd((XmlSchemaSet)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── E2E: Forced-format pipeline tests ──

    [Fact]
    public async Task ValidateJsonSchema_Pipeline_Valid_PassesThrough()
    {
        var context = new RouteContext();
        context.AddComponent(new Route.Components.DirectComponent());

        var route = new RouteDefinition();
        route.From("direct:validated")
             .ValidateJsonSchema(PersonJsonSchema);

        var compiler = new RouteCompiler(context, null);
        var compiled = compiler.Compile(route);

        var exchange = CreateExchange(ValidPersonJson);
        await compiled.Process(exchange);

        ((bool)exchange.Properties[ValidateProcessor.ValidationResultProperty]!).Should().BeTrue();
    }

    [Fact]
    public async Task ValidateJsonSchema_Pipeline_Invalid_Throws()
    {
        var context = new RouteContext();
        context.AddComponent(new Route.Components.DirectComponent());

        var route = new RouteDefinition();
        route.From("direct:validated")
             .ValidateJsonSchema(PersonJsonSchema);

        var compiler = new RouteCompiler(context, null);
        var compiled = compiler.Compile(route);

        var exchange = CreateExchange(InvalidPersonJson_MissingAge);
        var act = async () => await compiled.Process(exchange);

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task ValidateJsonSchema_Object_Pipeline_Valid()
    {
        var context = new RouteContext();
        context.AddComponent(new Route.Components.DirectComponent());
        var schema = JsonSchema.FromText(PersonJsonSchema);

        var route = new RouteDefinition();
        route.From("direct:validated")
             .ValidateJsonSchema(schema);

        var compiler = new RouteCompiler(context, null);
        var compiled = compiler.Compile(route);

        var exchange = CreateExchange(ValidPersonJson);
        await compiled.Process(exchange);

        ((bool)exchange.Properties[ValidateProcessor.ValidationResultProperty]!).Should().BeTrue();
    }

    [Fact]
    public async Task ValidateXsd_Pipeline_Valid_PassesThrough()
    {
        var context = new RouteContext();
        context.AddComponent(new Route.Components.DirectComponent());

        var route = new RouteDefinition();
        route.From("direct:validated")
             .ValidateXsd(PersonXsd);

        var compiler = new RouteCompiler(context, null);
        var compiled = compiler.Compile(route);

        var exchange = CreateExchange(ValidPersonXml);
        await compiled.Process(exchange);

        ((bool)exchange.Properties[ValidateProcessor.ValidationResultProperty]!).Should().BeTrue();
    }

    [Fact]
    public async Task ValidateXsd_Pipeline_Invalid_Throws()
    {
        var context = new RouteContext();
        context.AddComponent(new Route.Components.DirectComponent());

        var route = new RouteDefinition();
        route.From("direct:validated")
             .ValidateXsd(PersonXsd);

        var compiler = new RouteCompiler(context, null);
        var compiled = compiler.Compile(route);

        var exchange = CreateExchange(InvalidPersonXml_MissingAge);
        var act = async () => await compiled.Process(exchange);

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task ValidateXsd_Namespace_Pipeline_Valid()
    {
        var context = new RouteContext();
        context.AddComponent(new Route.Components.DirectComponent());

        var route = new RouteDefinition();
        route.From("direct:validated")
             .ValidateXsd(null, PersonXsd);

        var compiler = new RouteCompiler(context, null);
        var compiled = compiler.Compile(route);

        var exchange = CreateExchange(ValidPersonXml);
        await compiled.Process(exchange);

        ((bool)exchange.Properties[ValidateProcessor.ValidationResultProperty]!).Should().BeTrue();
    }

    [Fact]
    public async Task ValidateXsd_SchemaSet_Pipeline_Valid()
    {
        var context = new RouteContext();
        context.AddComponent(new Route.Components.DirectComponent());

        var schemaSet = new XmlSchemaSet();
        using var reader = new StringReader(PersonXsd);
        schemaSet.Add("", System.Xml.XmlReader.Create(reader));
        schemaSet.Compile();

        var route = new RouteDefinition();
        route.From("direct:validated")
             .ValidateXsd(schemaSet);

        var compiler = new RouteCompiler(context, null);
        var compiled = compiler.Compile(route);

        var exchange = CreateExchange(ValidPersonXml);
        await compiled.Process(exchange);

        ((bool)exchange.Properties[ValidateProcessor.ValidationResultProperty]!).Should().BeTrue();
    }

    [Fact]
    public async Task ValidateJsonSchema_SoftMode_Pipeline_Invalid_NoThrow()
    {
        var context = new RouteContext();
        context.AddComponent(new Route.Components.DirectComponent());

        var route = new RouteDefinition();
        route.From("direct:validated")
             .ValidateJsonSchema(PersonJsonSchema, throwOnFailure: false);

        var compiler = new RouteCompiler(context, null);
        var compiled = compiler.Compile(route);

        var exchange = CreateExchange(InvalidPersonJson_MissingAge);
        await compiled.Process(exchange);

        ((bool)exchange.Properties[ValidateProcessor.ValidationResultProperty]!).Should().BeFalse();
        exchange.Properties[ValidateProcessor.ValidationErrorsProperty].Should().NotBeNull();
    }

    [Fact]
    public async Task ValidateXsd_SoftMode_Pipeline_Invalid_NoThrow()
    {
        var context = new RouteContext();
        context.AddComponent(new Route.Components.DirectComponent());

        var route = new RouteDefinition();
        route.From("direct:validated")
             .ValidateXsd(PersonXsd, throwOnFailure: false);

        var compiler = new RouteCompiler(context, null);
        var compiled = compiler.Compile(route);

        var exchange = CreateExchange(InvalidPersonXml_MissingAge);
        await compiled.Process(exchange);

        ((bool)exchange.Properties[ValidateProcessor.ValidationResultProperty]!).Should().BeFalse();
        exchange.Properties[ValidateProcessor.ValidationErrorsProperty].Should().NotBeNull();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static string CreateTempFile(string content, string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"redb_test_{Guid.NewGuid()}{extension}");
        File.WriteAllText(path, content);
        return path;
    }
}
