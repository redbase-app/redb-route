using AwesomeAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.DataFormats.Avro;
using redb.Route.DataFormats.Csv;
using redb.Route.DataFormats.Mime;
using redb.Route.DataFormats.Protobuf;
using redb.Route.DataFormats.XmlJson;
using redb.Route.DataFormats.Yaml;

namespace redb.Route.Tests.DataFormats;

/// <summary>
/// Every data format package registers its name, as Apache Camel names it, beside its content type: after
/// <c>AddCsvDataFormat()</c>, <c>&lt;marshal format="csv"/&gt;</c> finds it. Until 4.7.0 a name was registered only
/// through <c>&lt;dataFormat name="..."&gt;</c>.
/// </summary>
public sealed class DataFormatNameTests
{
    public static TheoryData<string, Action<IRouteContext>, Type> Formats => new()
    {
        { "csv", c => c.AddCsvDataFormat(), typeof(CsvDataFormat) },
        { "yaml", c => c.AddYamlDataFormat(), typeof(YamlDataFormat) },
        { "avro", c => c.AddAvroDataFormat(), typeof(AvroDataFormat) },
        { "protobuf", c => c.AddProtobufDataFormat(), typeof(ProtobufDataFormat) },
        { "mimeMultipart", c => c.AddMimeMultipartDataFormat(), typeof(MimeMultipartDataFormat) },
        { "xmljson", c => c.AddXmlJsonDataFormat(), typeof(XmlJsonDataFormat) },
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task TheNameResolves_AfterTheRegistrationExtension(string name, Action<IRouteContext> register, Type type)
    {
        await using var context = new RouteContext();
        register(context);

        context.GetService<IDataFormatRegistry>()!.GetSerializer(name).Should().BeOfType(type);
        type.GetField("Name")!.GetRawConstantValue().Should().Be(name);
    }
}
