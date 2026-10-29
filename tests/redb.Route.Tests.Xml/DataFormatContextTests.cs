using System.Text;
using System.Xml.Linq;
using AwesomeAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Serialization;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml;

/// <summary>A format with one option, as the redb.Route.DataFormats.* serializers are.</summary>
public sealed class ProbeDataFormat : IMessageSerializer
{
    public string Prefix { get; set; } = "plain";

    public string ContentType => "application/x-probe";

    public byte[] Serialize<T>(T value) => Encoding.UTF8.GetBytes($"{Prefix}:{value}");

    public T? Deserialize<T>(byte[] data) => (T?)Deserialize(data, typeof(T));

    public object? Deserialize(byte[] data, Type type) => Encoding.UTF8.GetString(data);
}

/// <summary>The options of <see cref="OptionsBuiltFormat"/>, as CsvDataFormatOptions is for CsvDataFormat.</summary>
public sealed class OptionsBuiltFormatOptions
{
    public string Delimiter { get; set; } = ",";
}

/// <summary>Built from an options object only, the shape of the redb.Route.DataFormats.* serializers.</summary>
public sealed class OptionsBuiltFormat(OptionsBuiltFormatOptions? options = null) : IMessageSerializer
{
    private readonly OptionsBuiltFormatOptions _options = options ?? new();

    public string ContentType => "text/x-built";

    public byte[] Serialize<T>(T value) => Encoding.UTF8.GetBytes($"a{_options.Delimiter}{value}");

    public T? Deserialize<T>(byte[] data) => (T?)Deserialize(data, typeof(T));

    public object? Deserialize(byte[] data, Type type) => Encoding.UTF8.GetString(data);
}

/// <summary>
/// <c>&lt;dataFormat&gt;</c> in <c>context.xml</c> registers a format for <c>&lt;marshal format="…"/&gt;</c>, as
/// <c>context.AddDataFormat(...)</c> does in code. Before, a module in markup could not register one, and on the Tsak
/// worker <c>&lt;marshal format="csv"/&gt;</c> failed with "Data format 'csv' is not registered".
/// </summary>
public sealed class DataFormatContextTests : IAsyncLifetime
{
    private static readonly string ProbeType = $"{typeof(ProbeDataFormat).FullName}, {typeof(ProbeDataFormat).Assembly.GetName().Name}";

    private readonly RouteContext _context = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _context.DisposeAsync();

    private void LoadContext(string inner)
        => new XmlContextLoader(_context).Load(XDocument.Parse($"""
            <context xmlns="urn:redb:route:1.0">
              {inner}
            </context>
            """, LoadOptions.SetLineInfo), "context.xml");

    private async Task<object?> MarshalAsync(string format)
    {
        object? body = null;
        _context.AddRoutes(r => r.From("direct://df-out").Process(e => body = e.In.Body));
        _context.AddXmlRoutesFromContent($"""
            <routes xmlns="urn:redb:route:1.0">
              <route id="df">
                <from uri="direct://df-in"/>
                <marshal format="{format}"/>
                <to uri="direct://df-out"/>
              </route>
            </routes>
            """);
        await _context.Start();
        var producer = _context.GetEndpoint("direct://df-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("order")));
        return body;
    }

    [Fact]
    public async Task A_format_declared_in_context_xml_is_found_by_its_name_with_its_options()
    {
        LoadContext($"""<dataFormat type="{ProbeType}" name="probe" prefix="csv-like"/>""");

        var body = await MarshalAsync("probe");

        Encoding.UTF8.GetString((byte[])body!).Should().Be("csv-like:order");
    }

    [Fact]
    public async Task A_format_is_also_found_by_its_own_content_type()
    {
        LoadContext($"""<dataFormat type="{ProbeType}"/>""");

        var body = await MarshalAsync("application/x-probe");

        Encoding.UTF8.GetString((byte[])body!).Should().Be("plain:order");
    }

    [Fact]
    public async Task A_format_built_from_an_options_object_gets_its_options_from_the_attributes()
    {
        var built = $"{typeof(OptionsBuiltFormat).FullName}, {typeof(OptionsBuiltFormat).Assembly.GetName().Name}";
        LoadContext($"""<dataFormat type="{built}" name="csv" delimiter=";"/>""");

        var body = await MarshalAsync("csv");

        Encoding.UTF8.GetString((byte[])body!).Should().Be("a;order",
            "the format has no parameterless constructor; the attributes set its options object");
    }

    [Fact]
    public void A_type_that_is_not_a_serializer_is_an_error_of_the_document()
    {
        var act = () => LoadContext("""<dataFormat type="System.String"/>""");

        act.Should().Throw<XmlRouteException>().Which.Errors.Should().Contain(e => e.Contains("IMessageSerializer"));
    }
}
