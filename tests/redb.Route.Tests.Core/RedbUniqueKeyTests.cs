using System.Text;
using redb.Route.RedbCore;

namespace redb.Route.Tests.Core;

/// <summary>Unit tests for <see cref="RedbUniqueKey"/> — the shared 440-safe key normalizer.</summary>
public sealed class RedbUniqueKeyTests
{
    [Fact]
    public void Normalize_ShortKey_PassesThroughVerbatim()
    {
        RedbUniqueKey.Normalize("proc:key-1").Should().Be("proc:key-1");
    }

    [Fact]
    public void Normalize_SurrogatePairAtCutPoint_NeverSplitsThePair()
    {
        // Index 374 holds the high half of an emoji: a naive raw[..375] slice would end
        // the prefix with a lone high surrogate — not encodable to UTF-8, so the
        // provider would reject the row instead of saving the key.
        var raw = new string('a', 374) + "\U0001F600" + new string('b', 100);
        var normalized = RedbUniqueKey.Normalize(raw);

        normalized.Length.Should().BeLessThanOrEqualTo(RedbUniqueKey.MaxLength);

        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var encode = () => strictUtf8.GetBytes(normalized);
        encode.Should().NotThrow("the stored key must be valid UTF-16 with no lone surrogates");

        // Deterministic and still collision-resistant past the readable prefix.
        RedbUniqueKey.Normalize(raw).Should().Be(normalized);
        RedbUniqueKey.Normalize(raw + "x").Should().NotBe(normalized);
    }

    [Fact]
    public void Normalize_SurrogatePairBeforeCutPoint_KeepsFullLength()
    {
        // The pair sits fully inside the prefix — nothing to trim, exact 440 shape.
        var raw = "\U0001F600" + new string('a', 500);
        var normalized = RedbUniqueKey.Normalize(raw);

        normalized.Length.Should().Be(RedbUniqueKey.MaxLength);
        normalized.Should().Contain("#");
    }
}
