using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using redb.Route.Tcp;

namespace redb.Route.Tests.Tcp;

/// <summary>
/// Unit tests for TcpCodec framing logic using in-memory streams.
/// </summary>
public class TcpCodecTests
{
    // ── TextLine framing ──

    [Fact]
    public async Task TextLine_WriteAndRead_Roundtrip()
    {
        var ms = new MemoryStream();
        var data = Encoding.UTF8.GetBytes("hello world");

        await TcpCodec.WriteMessageAsync(ms, data, TcpFraming.TextLine, "\n", Encoding.UTF8, CancellationToken.None);

        ms.Position = 0;
        var result = await TcpCodec.ReadMessageAsync(ms, TcpFraming.TextLine, "\n", 8192, CancellationToken.None);

        result.Should().NotBeNull();
        Encoding.UTF8.GetString(result!).Should().Be("hello world");
    }

    [Fact]
    public async Task TextLine_CustomDelimiter_Roundtrip()
    {
        var ms = new MemoryStream();
        var data = Encoding.UTF8.GetBytes("line1");

        await TcpCodec.WriteMessageAsync(ms, data, TcpFraming.TextLine, "|", Encoding.UTF8, CancellationToken.None);

        ms.Position = 0;
        var result = await TcpCodec.ReadMessageAsync(ms, TcpFraming.TextLine, "|", 8192, CancellationToken.None);

        result.Should().NotBeNull();
        Encoding.UTF8.GetString(result!).Should().Be("line1");
    }

    [Fact]
    public async Task TextLine_MultipleMessages_ReadSequentially()
    {
        var ms = new MemoryStream();

        await TcpCodec.WriteMessageAsync(ms, "msg1"u8.ToArray(), TcpFraming.TextLine, "\n", Encoding.UTF8, CancellationToken.None);
        await TcpCodec.WriteMessageAsync(ms, "msg2"u8.ToArray(), TcpFraming.TextLine, "\n", Encoding.UTF8, CancellationToken.None);

        ms.Position = 0;
        var r1 = await TcpCodec.ReadMessageAsync(ms, TcpFraming.TextLine, "\n", 8192, CancellationToken.None);
        var r2 = await TcpCodec.ReadMessageAsync(ms, TcpFraming.TextLine, "\n", 8192, CancellationToken.None);

        Encoding.UTF8.GetString(r1!).Should().Be("msg1");
        Encoding.UTF8.GetString(r2!).Should().Be("msg2");
    }

    [Fact]
    public async Task TextLine_EmptyStream_ReturnsNull()
    {
        var ms = new MemoryStream();
        var result = await TcpCodec.ReadMessageAsync(ms, TcpFraming.TextLine, "\n", 8192, CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task TextLine_CrLfDelimiter_Works()
    {
        var ms = new MemoryStream();
        var data = Encoding.UTF8.GetBytes("hello");

        await TcpCodec.WriteMessageAsync(ms, data, TcpFraming.TextLine, "\r\n", Encoding.UTF8, CancellationToken.None);

        ms.Position = 0;
        var result = await TcpCodec.ReadMessageAsync(ms, TcpFraming.TextLine, "\r\n", 8192, CancellationToken.None);

        Encoding.UTF8.GetString(result!).Should().Be("hello");
    }

    // ── LengthPrefixed framing ──

    [Fact]
    public async Task LengthPrefixed_WriteAndRead_Roundtrip()
    {
        var ms = new MemoryStream();
        var data = new byte[] { 1, 2, 3, 4, 5 };

        await TcpCodec.WriteMessageAsync(ms, data, TcpFraming.LengthPrefixed, "\n", Encoding.UTF8, CancellationToken.None);

        ms.Position = 0;
        var result = await TcpCodec.ReadMessageAsync(ms, TcpFraming.LengthPrefixed, "\n", 8192, CancellationToken.None);

        result.Should().BeEquivalentTo(data);
    }

    [Fact]
    public async Task LengthPrefixed_VerifyHeader_BigEndian()
    {
        var ms = new MemoryStream();
        var data = new byte[300];
        new Random(42).NextBytes(data);

        await TcpCodec.WriteMessageAsync(ms, data, TcpFraming.LengthPrefixed, "\n", Encoding.UTF8, CancellationToken.None);

        ms.Position = 0;
        var header = new byte[4];
        await ms.ReadExactlyAsync(header);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);

        length.Should().Be(300);
    }

    [Fact]
    public async Task LengthPrefixed_EmptyPayload_Roundtrip()
    {
        var ms = new MemoryStream();
        var data = Array.Empty<byte>();

        await TcpCodec.WriteMessageAsync(ms, data, TcpFraming.LengthPrefixed, "\n", Encoding.UTF8, CancellationToken.None);

        ms.Position = 0;
        var result = await TcpCodec.ReadMessageAsync(ms, TcpFraming.LengthPrefixed, "\n", 8192, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task LengthPrefixed_MultipleMessages_ReadSequentially()
    {
        var ms = new MemoryStream();
        var d1 = new byte[] { 10, 20, 30 };
        var d2 = new byte[] { 40, 50 };

        await TcpCodec.WriteMessageAsync(ms, d1, TcpFraming.LengthPrefixed, "\n", Encoding.UTF8, CancellationToken.None);
        await TcpCodec.WriteMessageAsync(ms, d2, TcpFraming.LengthPrefixed, "\n", Encoding.UTF8, CancellationToken.None);

        ms.Position = 0;
        var r1 = await TcpCodec.ReadMessageAsync(ms, TcpFraming.LengthPrefixed, "\n", 8192, CancellationToken.None);
        var r2 = await TcpCodec.ReadMessageAsync(ms, TcpFraming.LengthPrefixed, "\n", 8192, CancellationToken.None);

        r1.Should().BeEquivalentTo(d1);
        r2.Should().BeEquivalentTo(d2);
    }

    [Fact]
    public async Task LengthPrefixed_EmptyStream_ReturnsNull()
    {
        var ms = new MemoryStream();
        var result = await TcpCodec.ReadMessageAsync(ms, TcpFraming.LengthPrefixed, "\n", 8192, CancellationToken.None);
        result.Should().BeNull();
    }

    // ── Raw framing ──

    [Fact]
    public async Task Raw_WriteAndRead_Roundtrip()
    {
        var ms = new MemoryStream();
        var data = new byte[] { 0xAA, 0xBB, 0xCC };

        await TcpCodec.WriteMessageAsync(ms, data, TcpFraming.Raw, "\n", Encoding.UTF8, CancellationToken.None);

        ms.Position = 0;
        var result = await TcpCodec.ReadMessageAsync(ms, TcpFraming.Raw, "\n", 8192, CancellationToken.None);

        result.Should().BeEquivalentTo(data);
    }

    [Fact]
    public async Task Raw_EmptyStream_ReturnsNull()
    {
        var ms = new MemoryStream();
        var result = await TcpCodec.ReadMessageAsync(ms, TcpFraming.Raw, "\n", 8192, CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task Raw_LargePayload_ReadFully()
    {
        var data = new byte[4096];
        new Random(42).NextBytes(data);
        var ms = new MemoryStream(data);

        var result = await TcpCodec.ReadMessageAsync(ms, TcpFraming.Raw, "\n", 8192, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Length.Should().BeGreaterThan(0);
    }
}
