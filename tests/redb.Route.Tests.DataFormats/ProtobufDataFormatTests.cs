using Google.Protobuf;
using redb.Route.Core;
using redb.Route.DataFormats.Protobuf;
using redb.Route.TestKit;
using redb.Route.Tests.DataFormats.Protos;

namespace redb.Route.Tests.DataFormats;

public class ProtobufDataFormatTests
{
    private static OrderMessage Sample() => new() { Id = 7, Customer = "acme", Skus = { "a", "b" } };

    [Fact]
    public void RoundTrip_GeneratedMessage()
    {
        var format = new ProtobufDataFormat();

        var bytes = format.Serialize(Sample());
        var back = format.Deserialize<OrderMessage>(bytes);

        bytes.Should().Equal(Sample().ToByteArray());
        back.Should().Be(Sample());
    }

    [Fact]
    public void ConfluentWireFormat_FramesAndUnframes()
    {
        var format = new ProtobufDataFormat(new ProtobufDataFormatOptions { ConfluentWireFormat = true, SchemaId = 0x01020304 });

        var framed = format.Serialize(Sample());

        framed.Take(6).Should().Equal(0, 1, 2, 3, 4, 0);
        framed.Skip(6).Should().Equal(Sample().ToByteArray());
        format.Deserialize<OrderMessage>(framed).Should().Be(Sample());

        var plain = new ProtobufDataFormat();
        var act = () => format.Deserialize<OrderMessage>(plain.Serialize(Sample()) is { Length: > 0 } p && p[0] != 0 ? p : [9, 9, 9, 9, 9, 9]);
        act.Should().Throw<InvalidOperationException>().WithMessage("Protobuf data format:*wire format*");
    }

    [Fact]
    public void NonMessageBody_And_NonMessageTarget_FailNamingTheFormat()
    {
        var format = new ProtobufDataFormat();

        var marshal = () => format.Serialize(new OrderRow());
        var unmarshal = () => format.Deserialize<OrderRow>([1, 2, 3]);

        marshal.Should().Throw<InvalidOperationException>().WithMessage("Protobuf data format:*IMessage*");
        unmarshal.Should().Throw<InvalidOperationException>().WithMessage("Protobuf data format:*IMessage*");
    }

    [Fact]
    public void Garbage_FailsNamingTheFormat()
    {
        var act = () => new ProtobufDataFormat().Deserialize<OrderMessage>([0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);

        act.Should().Throw<InvalidOperationException>().WithMessage("Protobuf data format:*");
    }

    [Fact]
    public async Task Dsl_And_Registry()
    {
        await using var ctx = new RouteContext();
        ctx.AddProtobufDataFormat();
        ctx.AddRoutes(b =>
        {
            b.From("direct://sugar").MarshalProtobuf().To("mock://wire").UnmarshalProtobuf<OrderMessage>();
            b.From("direct://named").Marshal("application/x-protobuf").Unmarshal<OrderMessage>("application/protobuf");
        });
        await ctx.Start();

        (await ctx.RequestBody<OrderMessage>("direct://sugar", Sample())).Should().Be(Sample());
        (await ctx.RequestBody<OrderMessage>("direct://named", Sample())).Should().Be(Sample());
        ctx.Mock("mock://wire").ReceivedExchanges[0].In.ContentType.Should().Be("application/x-protobuf");
    }
}
