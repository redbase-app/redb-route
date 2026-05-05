using System.Globalization;
using redb.Route.Firebase;

namespace redb.Route.Tests.Firebase;

public sealed class GcsDateTimeHelperTests
{
    [Fact]
    public void ValidRfc3339_ReturnsFromAccessor()
    {
        var expected = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

        var result = GcsDateTimeHelper.SafeParse(() => expected, "2025-06-15T12:00:00Z");

        result.Should().Be(expected);
    }

    [Fact]
    public void AccessorReturnsNull_ReturnsNull()
    {
        var result = GcsDateTimeHelper.SafeParse(() => null, null);

        result.Should().BeNull();
    }

    [Fact]
    public void AccessorThrows_FallsBackToRawParsing()
    {
        // fake-gcs-server may return non-RFC 3339 like "2025-06-15 12:30:00"
        var result = GcsDateTimeHelper.SafeParse(
            () => throw new FormatException("bad format"),
            "2025-06-15 12:30:00");

        result.Should().NotBeNull();
        result!.Value.UtcDateTime.Should().BeCloseTo(
            new DateTime(2025, 6, 15, 12, 30, 0, DateTimeKind.Utc),
            TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void AccessorThrows_RawIsRfc3339_ParsesCorrectly()
    {
        var result = GcsDateTimeHelper.SafeParse(
            () => throw new FormatException("bad format"),
            "2025-06-15T12:00:00.000Z");

        result.Should().NotBeNull();
        result!.Value.Should().Be(new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void AccessorThrows_RawIsNull_ReturnsNull()
    {
        var result = GcsDateTimeHelper.SafeParse(
            () => throw new FormatException("bad format"),
            null);

        result.Should().BeNull();
    }

    [Fact]
    public void AccessorThrows_RawIsGarbage_ReturnsNull()
    {
        var result = GcsDateTimeHelper.SafeParse(
            () => throw new FormatException("bad format"),
            "not-a-date-at-all");

        result.Should().BeNull();
    }

    [Fact]
    public void AccessorThrows_RawIsEmpty_ReturnsNull()
    {
        var result = GcsDateTimeHelper.SafeParse(
            () => throw new FormatException("bad format"),
            "");

        result.Should().BeNull();
    }

    [Fact]
    public void OnlyFormatException_TriggersRawFallback()
    {
        // Other exceptions should propagate, not be silently caught
        var act = () => GcsDateTimeHelper.SafeParse(
            () => throw new InvalidOperationException("unexpected"),
            "2025-06-15T12:00:00Z");

        act.Should().Throw<InvalidOperationException>();
    }
}
