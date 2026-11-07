using redb.Route.Core;
using redb.Route.DataFormats.Avro;
using redb.Route.TestKit;

namespace redb.Route.Tests.DataFormats;

public class AvroDataFormatTests
{
    private static readonly OrderRow Sample = new() { Id = 7, Customer = "acme", Amount = 12.5m };

    [Fact]
    public void RoundTrip_SchemaBuiltFromClrType()
    {
        var format = new AvroDataFormat();

        var bytes = format.Serialize(Sample);
        var back = format.Deserialize<OrderRow>(bytes);

        bytes.Should().NotBeEmpty();
        back.Should().Be(Sample);
        format.SchemaFor(typeof(OrderRow)).Should().BeOfType<Chr.Avro.Abstract.RecordSchema>();
    }

    [Fact]
    public void RoundTrip_List()
    {
        var format = new AvroDataFormat();
        var list = new List<OrderRow> { Sample, new() { Id = 8, Customer = "b", Amount = 1m } };

        format.Deserialize<List<OrderRow>>(format.Serialize(list)).Should().Equal(list);
    }

    [Fact]
    public void ExplicitSchema_IsUsed()
    {
        const string schema = """
            {"type":"record","name":"OrderRow","fields":[
              {"name":"Id","type":"int"},
              {"name":"Customer","type":"string"},
              {"name":"Amount","type":{"type":"bytes","logicalType":"decimal","precision":29,"scale":14}}
            ]}
            """;
        var format = new AvroDataFormat(new AvroDataFormatOptions { Schema = schema });

        format.Deserialize<OrderRow>(format.Serialize(Sample)).Should().Be(Sample);
        format.SchemaFor(typeof(OrderRow)).Should().BeOfType<Chr.Avro.Abstract.RecordSchema>().Which.Name.Should().Be("OrderRow");
    }

    [Fact]
    public void ConfluentWireFormat_FramesAndUnframes()
    {
        var format = new AvroDataFormat(new AvroDataFormatOptions { ConfluentWireFormat = true, SchemaId = 7 });

        var framed = format.Serialize(Sample);

        framed.Take(5).Should().Equal(0, 0, 0, 0, 7);
        format.Deserialize<OrderRow>(framed).Should().Be(Sample);

        var act = () => format.Deserialize<OrderRow>([1, 2, 3, 4, 5, 6]);
        act.Should().Throw<InvalidOperationException>().WithMessage("Avro data format:*wire format*");
    }

    [Fact]
    public void Garbage_FailsNamingTheFormat()
    {
        var act = () => new AvroDataFormat().Deserialize<OrderRow>([0xFF]);

        act.Should().Throw<InvalidOperationException>().WithMessage("Avro data format:*");
    }

    [Fact]
    public async Task Dsl_And_Registry()
    {
        await using var ctx = new RouteContext();
        ctx.AddAvroDataFormat();
        ctx.AddRoutes(b =>
        {
            b.From("direct://sugar").MarshalAvro().To("mock://wire").UnmarshalAvro<OrderRow>();
            b.From("direct://named").Marshal("application/avro").Unmarshal<OrderRow>("avro/binary");
        });
        await ctx.Start();

        (await ctx.RequestBody<OrderRow>("direct://sugar", Sample)).Should().Be(Sample);
        (await ctx.RequestBody<OrderRow>("direct://named", Sample)).Should().Be(Sample);
        ctx.Mock("mock://wire").ReceivedExchanges[0].In.ContentType.Should().Be("application/avro");
    }
}
