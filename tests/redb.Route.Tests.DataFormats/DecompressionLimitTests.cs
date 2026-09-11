using redb.Route.Serialization;

namespace redb.Route.Tests.DataFormats;

/// <summary>
/// Code review 2026-09-01 (В9): GZip and Zip refuse a body that expands past the configured cap, so a
/// small compressed message cannot exhaust the process memory. The default cap is 128 MB; a format
/// instance can be given its own.
/// </summary>
public class DecompressionLimitTests
{
    [Fact]
    public void GZip_RefusesABodyThatExpandsPastTheLimit()
    {
        var bomb = new GZipMessageSerializer().Serialize(new byte[2 * 1024 * 1024]);   // 2 MB of zeros, a few KB on the wire
        var capped = new GZipMessageSerializer(maxDecodedBytes: 1024 * 1024);

        var act = () => capped.Deserialize<byte[]>(bomb);

        act.Should().Throw<InvalidOperationException>().WithMessage("*exceeds the limit*");
        new GZipMessageSerializer().Deserialize<byte[]>(bomb).Should().HaveCount(2 * 1024 * 1024, "the default cap is far above 2 MB");
    }

    [Fact]
    public void Zip_RefusesAnEntryThatExpandsPastTheLimit()
    {
        var bomb = new ZipMessageSerializer().Serialize(new byte[2 * 1024 * 1024]);
        var capped = new ZipMessageSerializer(maxDecodedBytes: 1024 * 1024);

        var act = () => capped.Deserialize<byte[]>(bomb);

        act.Should().Throw<InvalidOperationException>().WithMessage("*exceeds the limit*");
    }

    [Fact]
    public void ANonPositiveLimit_IsRefused()
    {
        var gzip = () => new GZipMessageSerializer(maxDecodedBytes: 0);
        var zip = () => new ZipMessageSerializer(maxDecodedBytes: -1);

        gzip.Should().Throw<ArgumentOutOfRangeException>();
        zip.Should().Throw<ArgumentOutOfRangeException>();
    }
}
