using redb.Route.GenericFile;

namespace redb.Route.Tests.GenericFile;

/// <summary>Unit tests for the shared helpers used by every file transport.</summary>
public class GenericFileUtilsTests
{
    private readonly FakeFileOperations _ops = new();

    // ── IsWithinDirectory ───────────────────────────────────────────

    [Theory]
    [InlineData("/data/out", "/data/out", true)]
    [InlineData("/data/out", "/data/out/report.csv", true)]
    [InlineData("/data/out", "/data/out/sub/report.csv", true)]
    [InlineData("/data/out/", "/data/out/report.csv", true)]
    [InlineData("/data/out", "/data/outside/report.csv", false)]
    [InlineData("/data/out", "/data/out2/report.csv", false)]
    [InlineData("/data/out", "/data/report.csv", false)]
    [InlineData("/data/out", "/etc/passwd", false)]
    public void IsWithinDirectory_RespectsTheDirectoryBoundary(string basePath, string candidate, bool expected)
        => GenericFileUtils.IsWithinDirectory(basePath, candidate, '/', StringComparison.Ordinal)
            .Should().Be(expected);

    [Fact]
    public void IsWithinDirectory_HonoursTheRequestedCaseSensitivity()
    {
        GenericFileUtils.IsWithinDirectory("/data/out", "/DATA/OUT/x", '/', StringComparison.Ordinal)
            .Should().BeFalse();

        GenericFileUtils.IsWithinDirectory("/data/out", "/DATA/OUT/x", '/', StringComparison.OrdinalIgnoreCase)
            .Should().BeTrue();
    }

    // ── SubstituteFileTokens ────────────────────────────────────────

    [Theory]
    [InlineData("${file:name}.done", "order.csv.done")]
    [InlineData("${file:name.noext}.done", "order.done")]
    [InlineData("archive-${file:name.noext}", "archive-order")]
    [InlineData("${FILE:NAME}", "order.csv")]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    public void SubstituteFileTokens_ReplacesFileVariables(string pattern, string expected)
        => GenericFileUtils.SubstituteFileTokens("order.csv", pattern, _ops).Should().Be(expected);

    [Fact]
    public void SubstituteFileTokens_LeavesUnknownVariablesAlone()
        => GenericFileUtils.SubstituteFileTokens("order.csv", "${header.x}", _ops).Should().Be("${header.x}");

    // ── GlobMatch ───────────────────────────────────────────────────

    [Theory]
    [InlineData("order.csv", "*.csv", true)]
    [InlineData("ORDER.CSV", "*.csv", true)]
    [InlineData("order.xml", "*.csv,*.xml", true)]
    [InlineData("order.txt", "*.csv,*.xml", false)]
    [InlineData("data1.txt", "data?.txt", true)]
    [InlineData("data12.txt", "data?.txt", false)]
    public void GlobMatch_HandlesWildcardsAndLists(string input, string pattern, bool expected)
        => GenericFileUtils.GlobMatch(input, pattern).Should().Be(expected);
}
