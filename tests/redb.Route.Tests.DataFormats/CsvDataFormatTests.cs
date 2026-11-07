using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.DataFormats.Csv;
using redb.Route.TestKit;

namespace redb.Route.Tests.DataFormats;

public class CsvDataFormatTests
{
    private static readonly List<OrderRow> Rows =
    [
        new() { Id = 1, Customer = "Smith, \"Bob\"\nJr", Amount = 10.5m },
        new() { Id = 2, Customer = "", Amount = 0m },
        new() { Id = 3, Customer = "Ünïcödé", Amount = 1234567.89m },
    ];

    [Fact]
    public void RoundTrip_PocoList_QuotesNewlinesEmptyUnicode()
    {
        var csv = new CsvDataFormat();

        var bytes = csv.Serialize(Rows);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        var back = csv.Deserialize<List<OrderRow>>(bytes);

        text.Should().StartWith("Id,Customer,Amount");
        text.Should().Contain("\"Smith, \"\"Bob\"\"\nJr\"", "RFC 4180 quoting");
        back.Should().Equal(Rows);
    }

    [Fact]
    public void RoundTrip_Array_And_IEnumerable_Targets()
    {
        var csv = new CsvDataFormat();
        var bytes = csv.Serialize(Rows);

        csv.Deserialize<OrderRow[]>(bytes).Should().Equal(Rows);
        csv.Deserialize<IEnumerable<OrderRow>>(bytes).Should().Equal(Rows);
        csv.Deserialize<OrderRow>(bytes).Should().Be(Rows[0], "a single target takes the first record");
    }

    [Fact]
    public void Maps_WithHeader_AndRows_WithoutHeader()
    {
        var withHeader = new CsvDataFormat();
        var maps = withHeader.Deserialize<List<Dictionary<string, string>>>("Id,Name\n1,a\n2,b\n"u8.ToArray())!;
        maps.Should().HaveCount(2);
        maps[1]["Name"].Should().Be("b");

        var noHeader = new CsvDataFormat(new CsvDataFormatOptions { HasHeaderRecord = false });
        var rows = noHeader.Deserialize<List<string[]>>("1,a\n2,b\n"u8.ToArray())!;
        rows.Should().HaveCount(2);
        rows[0].Should().Equal("1", "a");

        var back = System.Text.Encoding.UTF8.GetString(withHeader.Serialize(maps));
        back.Should().StartWith("Id,Name").And.Contain("2,b");

        var rawBack = System.Text.Encoding.UTF8.GetString(noHeader.Serialize(new List<string[]> { new[] { "x", "y" } }));
        rawBack.Trim().Should().Be("x,y");
    }

    [Fact]
    public void Delimiter_Option()
    {
        var csv = new CsvDataFormat(new CsvDataFormatOptions { Delimiter = ";" });

        var text = System.Text.Encoding.UTF8.GetString(csv.Serialize(Rows.Take(1)));

        text.Should().StartWith("Id;Customer;Amount");
        csv.Deserialize<List<OrderRow>>(System.Text.Encoding.UTF8.GetBytes(text))![0].Should().Be(Rows[0]);
    }

    [Fact]
    public void BadInput_FailsNamingTheFormat()
    {
        var csv = new CsvDataFormat();

        var act = () => csv.Deserialize<List<OrderRow>>("Id,Customer,Amount\nnot-a-number,x,1\n"u8.ToArray());

        act.Should().Throw<InvalidOperationException>().WithMessage("CSV data format:*");
    }

    [Fact]
    public async Task Dsl_PerNodeOptions_RoundTripThroughRoute()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://csv")
                .MarshalCsv(o => o.Delimiter = ";")
                .To("mock://wire")
                .UnmarshalCsv<List<OrderRow>>(o => o.Delimiter = ";")
                .To("mock://out"));
        await ctx.Start();

        await ctx.SendBody("direct://csv", Rows);

        var wire = ctx.Mock("mock://wire").ReceivedExchanges[0].In;
        wire.ContentType.Should().Be("text/csv");
        wire.Headers["Content-Type"].Should().Be("text/csv");
        ctx.Mock("mock://out").ReceivedExchanges[0].In.Body.Should().BeEquivalentTo(Rows);
    }

    [Fact]
    public async Task Registry_ContentTypeAddressing_And_ContentTypeDrivenUnmarshal()
    {
        await using var ctx = new RouteContext();
        ctx.AddCsvDataFormat();
        ctx.AddRoutes(b =>
        {
            b.From("direct://by-name").Marshal("text/csv").Unmarshal<List<OrderRow>>("text/csv");
            b.From("direct://by-content-type").Marshal("text/csv").Unmarshal<List<OrderRow>>();   // driven by ContentType = text/csv
        });
        await ctx.Start();

        (await ctx.RequestBody<List<OrderRow>>("direct://by-name", Rows)).Should().Equal(Rows);
        (await ctx.RequestBody<List<OrderRow>>("direct://by-content-type", Rows)).Should().Equal(Rows);

        var registry = ctx.GetService<IDataFormatRegistry>()!;
        registry.GetSerializer("text/csv; charset=utf-8").Should().BeOfType<CsvDataFormat>();
        registry.GetSerializer("application/csv").Should().BeOfType<CsvDataFormat>();
    }

    [Fact]
    public async Task UnregisteredFormat_FailsStart()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b.From("direct://nf").Marshal("text/csv"));

        var act = () => ctx.Start();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*'text/csv' is not registered*");
    }

    [Fact]
    public async Task StringBody_IsUnmarshalled()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://text").UnmarshalCsv<List<Dictionary<string, string>>>());
        await ctx.Start();

        var maps = await ctx.RequestBody<List<Dictionary<string, string>>>("direct://text", "a,b\n1,2\n");

        maps.Should().ContainSingle().Which["b"].Should().Be("2");
    }
}
