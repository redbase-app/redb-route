using System.IO.Compression;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Serialization;
using redb.Route.TestKit;

namespace redb.Route.Tests.DataFormats;

/// <summary>Base64 / GZip / Zip ship with the core and are registered by default.</summary>
public class CoreWrapperFormatsTests
{
    [Theory]
    [InlineData("application/base64", typeof(Base64MessageSerializer))]
    [InlineData("application/gzip", typeof(GZipMessageSerializer))]
    [InlineData("application/x-gzip", typeof(GZipMessageSerializer))]
    [InlineData("application/zip", typeof(ZipMessageSerializer))]
    public void RegisteredByDefault(string contentType, Type serializer)
    {
        using var ctx = new RouteContext();

        ctx.GetService<IDataFormatRegistry>()!.GetSerializer(contentType).Should().BeOfType(serializer);
    }

    [Fact]
    public void Base64_RoundTrip_AndInvalidInput()
    {
        var b64 = new Base64MessageSerializer();

        var encoded = b64.Serialize("hello, world");
        System.Text.Encoding.ASCII.GetString(encoded).Should().Be("aGVsbG8sIHdvcmxk");
        b64.Deserialize<string>(encoded).Should().Be("hello, world");
        b64.Deserialize<byte[]>(encoded).Should().Equal("hello, world"u8.ToArray());

        var act = () => b64.Deserialize<string>("not base64!"u8.ToArray());
        act.Should().Throw<InvalidOperationException>().WithMessage("Base64 data format:*");
    }

    [Fact]
    public void GZip_RoundTrip_ProducesGZipMagic()
    {
        var gzip = new GZipMessageSerializer();
        var payload = new string('x', 10_000);

        var compressed = gzip.Serialize(payload);

        compressed.Take(2).Should().Equal(0x1F, 0x8B);
        compressed.Length.Should().BeLessThan(payload.Length);
        gzip.Deserialize<string>(compressed).Should().Be(payload);
        gzip.Deserialize<Stream>(compressed).Should().BeReadable();
    }

    [Fact]
    public void Zip_RoundTrip_SingleEntry()
    {
        var zip = new ZipMessageSerializer("payload.txt");

        var bytes = zip.Serialize("inside");

        using (var archive = new ZipArchive(new MemoryStream(bytes)))
            archive.Entries.Should().ContainSingle().Which.Name.Should().Be("payload.txt");
        zip.Deserialize<string>(bytes).Should().Be("inside");
    }

    [Fact]
    public void WrongShapes_FailNamingTheFormat()
    {
        var gzip = new GZipMessageSerializer();

        var marshal = () => gzip.Serialize(new OrderRow());
        var unmarshal = () => gzip.Deserialize<int>(gzip.Serialize("1"));

        marshal.Should().Throw<InvalidOperationException>().WithMessage("GZip data format*byte[], string or Stream*");
        unmarshal.Should().Throw<InvalidOperationException>().WithMessage("GZip data format*byte[], string or Stream*");
    }

    [Fact]
    public async Task Dsl_MarshalByContentType_RoundTrip()
    {
        await using var ctx = new RouteContext().AddRoutes(b =>
        {
            b.From("direct://gz").Marshal("application/gzip").To("mock://wire");
            b.From("direct://gunzip").Unmarshal<string>("application/gzip");
        });
        await ctx.Start();

        await ctx.SendBody("direct://gz", "compress me");

        var wire = ctx.Mock("mock://wire").ReceivedExchanges[0].In;
        wire.ContentType.Should().Be("application/gzip");
        var compressed = (byte[])wire.Body!;
        compressed.Take(2).Should().Equal(0x1F, 0x8B);
        (await ctx.RequestBody<string>("direct://gunzip", compressed)).Should().Be("compress me");
    }

    // Code review 2026-09-01 (В5, В9 and the Zip entry item).

    [Fact]
    public async Task Dsl_Marshal_CompressesAnAlreadyMarshalledBody()
    {
        await using var ctx = new RouteContext().AddRoutes(b =>
            b.From("direct://json-gz").Marshal("application/json").Marshal("application/gzip").To("mock://json-gz"));
        await ctx.Start();

        await ctx.SendBody("direct://json-gz", new { id = 7 });

        var wire = ctx.Mock("mock://json-gz").ReceivedExchanges[0].In;
        wire.ContentType.Should().Be("application/gzip", "a byte wrapper takes a byte[] body as its payload; Marshal must not pass it through");
        using var gunzip = new GZipStream(new MemoryStream((byte[])wire.Body!), CompressionMode.Decompress);
        using var reader = new StreamReader(gunzip);
        (await reader.ReadToEndAsync()).Should().Contain("\"id\":7");
    }

    [Fact]
    public void Zip_SkipsDirectoryEntries_AndRefusesAMultiEntryArchive()
    {
        using var withDir = new MemoryStream();
        using (var archive = new ZipArchive(withDir, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("dir/");
            using var w = new StreamWriter(archive.CreateEntry("dir/body.txt").Open());
            w.Write("payload");
        }
        new ZipMessageSerializer().Deserialize<string>(withDir.ToArray()).Should().Be("payload", "a directory entry carries no data");

        using var two = new MemoryStream();
        using (var archive = new ZipArchive(two, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var a = new StreamWriter(archive.CreateEntry("a.txt").Open())) a.Write("a");
            using (var b = new StreamWriter(archive.CreateEntry("b.txt").Open())) b.Write("b");
        }
        var act = () => new ZipMessageSerializer().Deserialize<string>(two.ToArray());
        act.Should().Throw<InvalidOperationException>().WithMessage("*2 entries*");
    }

    [Fact]
    public async Task PositionedStreamBody_IsReadFromItsPosition()
    {
        var gz = new GZipMessageSerializer().Serialize("positioned");
        var stream = new MemoryStream();          // expandable: exposes its buffer, the shortcut's case
        stream.Write([0xFF, 0xFF, .. gz]);
        stream.Position = 2;   // two bytes of framing already consumed by an earlier step
        await using var ctx = new RouteContext().AddRoutes(b => b.From("direct://pos").Unmarshal<string>("application/gzip"));
        await ctx.Start();

        (await ctx.RequestBody<string>("direct://pos", stream)).Should().Be("positioned", "the MemoryStream shortcut must not hand over bytes before the position");
    }

    [Fact]
    public async Task Marshal_WithInstance_AllowsPerNodeOptions()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://zip")
                .Marshal(new ZipMessageSerializer("report.csv"))
                .Unmarshal<string>(new ZipMessageSerializer()));
        await ctx.Start();

        (await ctx.RequestBody<string>("direct://zip", "a,b")).Should().Be("a,b");
    }

    [Fact]
    public async Task StreamBody_IsUnmarshalled()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://stream").Unmarshal<string>("application/base64"));
        await ctx.Start();

        var back = await ctx.RequestBody<string>("direct://stream", new MemoryStream("aGk="u8.ToArray()));

        back.Should().Be("hi");
    }
}
