using System.Buffers.Binary;
using System.IO.Compression;
using redb.Route.Grpc;

namespace redb.Route.Tests.Grpc;

/// <summary>Unit-level checks of the wire codec itself, isolated from Kestrel and the client.</summary>
public class GrpcWireTests
{
    [Fact]
    public async Task Gzipped_frame_round_trips()
    {
        var message = System.Text.Encoding.UTF8.GetBytes(new string('a', 1000));

        using var compressedBody = new MemoryStream();
        using (var gzip = new GZipStream(compressedBody, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(message, 0, message.Length);
        var payload = compressedBody.ToArray();

        var frame = new byte[5 + payload.Length];
        frame[0] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1, 4), (uint)payload.Length);
        payload.CopyTo(frame, 5);

        var read = await GrpcWire.ReadMessageAsync(new MemoryStream(frame), 4 * 1024 * 1024, "gzip", default);

        read.Should().Equal(message);
    }

    [Fact]
    public async Task Compressed_frame_without_a_supported_encoding_is_unimplemented()
    {
        var frame = new byte[5];
        frame[0] = 1;

        var act = async () => await GrpcWire.ReadMessageAsync(new MemoryStream(frame), 0, null, default);

        (await act.Should().ThrowAsync<GrpcProtocolException>())
            .Which.Status.Should().Be(global::Grpc.Core.StatusCode.Unimplemented);
    }

    [Fact]
    public void A_body_that_is_not_a_payload_is_refused_not_stringified()
    {
        // Running an arbitrary object through ToString() would put text like
        // "System.Collections.Generic.Dictionary`2[…]" on the wire with an OK status — the same
        // silent-garbage failure the SOAP consumer had with byte[]. Fail loudly and name the type.
        var act = () => GrpcWire.ToBytes(new Dictionary<string, object?> { ["error"] = "server_error" });

        (act.Should().Throw<GrpcProtocolException>())
            .Which.Message.Should().Contain("Dictionary");
    }

    [Fact]
    public void Bytes_and_strings_are_payloads()
    {
        GrpcWire.ToBytes("abc").Should().Equal(System.Text.Encoding.UTF8.GetBytes("abc"));
        GrpcWire.ToBytes(new byte[] { 1, 2 }).Should().Equal((byte)1, (byte)2);
        GrpcWire.ToBytes(null).Should().BeEmpty();
    }

    // ── deadlines ────────────────────────────────────────────

    [Theory]
    [InlineData("100u")]        // ordinary microsecond deadline
    [InlineData("5S")]
    [InlineData("250m")]
    public void A_well_formed_timeout_is_parsed(string header)
    {
        GrpcWire.ParseTimeout(header).Should().NotBeNull();
    }

    [Theory]
    [InlineData("9223372036854775807u")]   // long.MaxValue: *10 wraps to a tiny negative
    [InlineData("1000000000000000000u")]   // wraps to a huge negative
    [InlineData("922337203685477581u")]
    public void A_timeout_that_overflows_is_ignored_not_turned_into_a_negative_deadline(string header)
    {
        // ParseTimeout promises that an unreadable deadline never fails the call — it just means we do
        // not enforce one. The microsecond arm multiplied a caller-supplied long by 10 unchecked, so a
        // crafted header produced a NEGATIVE TimeSpan. The consumer then either cancelled the call
        // immediately or, for the larger values, threw ArgumentOutOfRangeException out of CancelAfter
        // before the try-block that exists to contain exactly this. Either way the promise was broken by
        // untrusted input.
        var parsed = GrpcWire.ParseTimeout(header);

        parsed.Should().BeNull("an overflowing deadline is unreadable, and unreadable means unenforced");
    }

    [Fact]
    public void A_parsed_timeout_is_never_negative()
    {
        // The property that matters downstream: CancelAfter rejects anything below -1ms, and a negative
        // deadline that does not throw silently cancels the call at once. Neither is "no deadline".
        foreach (var unit in new[] { "H", "M", "S", "m", "u", "n" })
        {
            foreach (var amount in new[] { "1", "1000", "9223372036854775807", "1000000000000000000" })
            {
                var parsed = GrpcWire.ParseTimeout(amount + unit);
                if (parsed is not null)
                    parsed.Value.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero, "header was '{0}'", amount + unit);
            }
        }
    }
}
