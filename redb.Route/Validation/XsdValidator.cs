using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using System.Xml.Serialization;
using redb.Route.Abstractions;

namespace redb.Route.Validation;

/// <summary>
/// Validates XML message bodies against an XSD (XML Schema Definition).
/// Uses the built-in <see cref="XmlSchemaSet"/> and <see cref="XmlReader"/> validation —
/// no external NuGet packages required.
/// <para>
/// The validator accepts body as <see cref="XDocument"/>, <see cref="XElement"/>,
/// <see cref="string"/> (raw XML), or a POCO (auto-serialized via <see cref="XmlSerializer"/>).
/// </para>
/// </summary>
public sealed class XsdValidator : IMessageValidator
{
    private readonly XmlSchemaSet _schemaSet;
    private readonly XmlReaderSettings _readerSettings;

    /// <summary>Creates a validator from a pre-built <see cref="XmlSchemaSet"/>.</summary>
    /// <param name="schemaSet">A compiled XML Schema set to validate against.</param>
    public XsdValidator(XmlSchemaSet schemaSet)
    {
        _schemaSet = schemaSet ?? throw new ArgumentNullException(nameof(schemaSet));
        if (!_schemaSet.IsCompiled)
            _schemaSet.Compile();

        _readerSettings = new XmlReaderSettings
        {
            ValidationType = ValidationType.Schema,
            Schemas = _schemaSet
        };
    }

    /// <summary>Creates a validator from an XSD string (target namespace auto-detected).</summary>
    /// <param name="xsdContent">XSD schema as a string.</param>
    public XsdValidator(string xsdContent)
        : this(BuildSchemaSet(xsdContent ?? throw new ArgumentNullException(nameof(xsdContent))))
    {
    }

    /// <summary>Creates a validator from an XSD string with an explicit target namespace.</summary>
    /// <param name="targetNamespace">Target namespace URI (or null / empty for no-namespace schemas).</param>
    /// <param name="xsdContent">XSD schema as a string.</param>
    public XsdValidator(string? targetNamespace, string xsdContent)
        : this(BuildSchemaSet(targetNamespace, xsdContent ?? throw new ArgumentNullException(nameof(xsdContent))))
    {
    }

    /// <inheritdoc />
    public ValidationResult Validate(IExchange exchange)
    {
        ArgumentNullException.ThrowIfNull(exchange, nameof(exchange));

        var body = exchange.In.Body;
        if (body is null)
            return ValidationResult.Failure("Message body is null");

        string xml;

        if (body is XDocument xdoc)
        {
            xml = xdoc.ToString(SaveOptions.DisableFormatting);
        }
        else if (body is XElement xel)
        {
            xml = xel.ToString(SaveOptions.DisableFormatting);
        }
        else if (body is string str)
        {
            xml = str;
        }
        else
        {
            // POCO → serialize to XML string
            try
            {
                var serializer = new XmlSerializer(body.GetType());
                using var sw = new StringWriter();
                using var xw = XmlWriter.Create(sw, new XmlWriterSettings
                {
                    OmitXmlDeclaration = true,
                    Indent = false
                });
                serializer.Serialize(xw, body);
                xml = sw.ToString();
            }
            catch (Exception ex)
            {
                return ValidationResult.Failure($"Cannot serialize body to XML: {ex.Message}");
            }
        }

        return ValidateXml(xml);
    }

    private ValidationResult ValidateXml(string xml)
    {
        var errors = new List<string>();

        var settings = _readerSettings.Clone();
        settings.ValidationEventHandler += (_, e) =>
        {
            var location = e.Exception != null
                ? $"Line {e.Exception.LineNumber}, Position {e.Exception.LinePosition}"
                : "Unknown location";
            errors.Add($"{location}: {e.Message}");
        };

        try
        {
            using var stringReader = new StringReader(xml);
            using var reader = XmlReader.Create(stringReader, settings);
            while (reader.Read()) { } // Force full read to trigger all validation events
        }
        catch (XmlSchemaValidationException ex)
        {
            errors.Add($"Line {ex.LineNumber}, Position {ex.LinePosition}: {ex.Message}");
        }
        catch (XmlException ex)
        {
            errors.Add($"XML parsing error: {ex.Message}");
        }

        return errors.Count == 0
            ? ValidationResult.Success()
            : ValidationResult.Failure(errors);
    }

    private static XmlSchemaSet BuildSchemaSet(string xsdContent)
    {
        return BuildSchemaSet(null, xsdContent);
    }

    private static XmlSchemaSet BuildSchemaSet(string? targetNamespace, string xsdContent)
    {
        var schemaSet = new XmlSchemaSet();
        using var reader = new StringReader(xsdContent);
        schemaSet.Add(targetNamespace ?? "", XmlReader.Create(reader));
        schemaSet.Compile();
        return schemaSet;
    }
}
