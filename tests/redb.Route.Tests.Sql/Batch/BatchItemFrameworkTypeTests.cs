using System.Text.Json;
using System.Xml.Linq;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.Batch;

/// <summary>
/// Batch items whose type comes from .NET itself are not POCOs: their properties are not record fields. An XML node carries no
/// named values, as in Apache Camel — its values are bound with <c>xpath</c> expressions; a JSON document is read as its JSON.
/// </summary>
public sealed class BatchItemFrameworkTypeTests : IDisposable
{
    private readonly SqliteTestHelper _db = new();

    public BatchItemFrameworkTypeTests() => _db.Execute("CREATE TABLE framework_items (id INTEGER NULL, val TEXT NULL)");

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Binder_XElementItems_NamedPlaceholder_FailsWithXPathHint()
    {
        var items = new List<XElement> { XElement.Parse("""<row id="1"><name>n1</name>text</row>""") };

        var thrown = await RunAsync("INSERT INTO framework_items (id, val) VALUES (:#id, :#value)", items,
            new() { ["param.id"] = "1" });

        thrown.Should().BeOfType<InvalidOperationException>(
                $"XElement.Value is not a field named 'value' ({Outcome.Describe(thrown)}, rows: {string.Join(", ", Rows())})")
            .Which.Message.Should().Contain("item 0").And.Contain(":#value").And.Contain("xpath");
        Rows().Should().BeEmpty();
    }

    [Fact]
    public async Task Binder_XElementItems_BindWithXPathExpressions()
    {
        var items = new List<XElement>
        {
            XElement.Parse("""<row id="1"><name>n1</name></row>"""),
            XElement.Parse("""<row id="2"><name>n2</name></row>"""),
        };

        var thrown = await RunAsync("INSERT INTO framework_items (id, val) VALUES (:#id, :#val)", items, new()
        {
            ["param.id"] = "${xpath('@id')}",
            ["param.val"] = "${xpath('name')}",
        });

        thrown.Should().BeNull(Outcome.Describe(thrown));
        Rows().Should().Equal("1|n1", "2|n2");
    }

    [Fact]
    public async Task Binder_JsonDocumentItems_BindByKeys()
    {
        using var first = JsonDocument.Parse("""{"id":1,"val":"a"}""");
        using var second = JsonDocument.Parse("""{"id":2,"val":null}""");

        var thrown = await RunAsync("INSERT INTO framework_items (id, val) VALUES (:#id, :#val)", new List<JsonDocument> { first, second });

        thrown.Should().BeNull(Outcome.Describe(thrown));
        Rows().Should().Equal("1|a", "2|");
    }

    [Fact]
    public async Task Binder_FrameworkObjectItem_HasNoNamedValues()
    {
        var items = new List<object> { new Uri("https://h.example:8443/p") };

        var thrown = await RunAsync("INSERT INTO framework_items (id, val) VALUES (:#port, :#host)", items);

        thrown.Should().BeOfType<InvalidOperationException>(
                $"Uri.Port and Uri.Host are not record fields ({Outcome.Describe(thrown)}, rows: {string.Join(", ", Rows())})")
            .Which.Message.Should().Contain("item 0").And.Contain(":#port");
        Rows().Should().BeEmpty();
    }

    [Fact]
    public async Task Binder_AnonymousTypeItems_BindByProperty()
    {
        var items = new List<object> { new { id = 1, val = "a" }, new { id = 2, val = (string?)null } };

        var thrown = await RunAsync("INSERT INTO framework_items (id, val) VALUES (:#id, :#val)", items);

        thrown.Should().BeNull(Outcome.Describe(thrown));
        Rows().Should().Equal("1|a", "2|");
    }

    private async Task<Exception?> RunAsync(string sql, object items, Dictionary<string, string>? options = null)
    {
        var parameters = new Dictionary<string, string> { ["outputType"] = "None", ["batchSize"] = "10" };
        if (options is not null)
            foreach (var (key, value) in options)
                parameters[key] = value;

        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, _db.CreateFactory(), sql, parameters);
        IExchange carrier = new Exchange(new Message(items));
        return await Outcome.Of(() => endpoint.CreateProducer().Process(carrier, CancellationToken.None));
    }

    private List<string> Rows() =>
        _db.Query("SELECT id, val FROM framework_items ORDER BY id").Select(r => $"{r["id"]}|{r["val"]}").ToList();
}
