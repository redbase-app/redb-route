using redb.Route.GenericFile;
using redb.Route.Sftp;

namespace redb.Route.Tests.Sftp;

public sealed class SftpConsumerHelperTests
{
    // ── GlobMatch ───────────────────────────────────────────────────

    [Theory]
    [InlineData("report.csv", "*.csv", true)]
    [InlineData("report.csv", "*.json", false)]
    [InlineData("report.csv", "*", true)]
    [InlineData("report.csv", "report.*", true)]
    [InlineData("report.csv", "report.csv", true)]
    [InlineData("REPORT.CSV", "*.csv", true)]  // case-insensitive
    [InlineData("data.json", "*.csv,*.json", true)]  // comma-separated
    [InlineData("readme.txt", "*.csv,*.json", false)]
    [InlineData("file1.txt", "file?.txt", true)]  // ? wildcard
    [InlineData("file10.txt", "file?.txt", false)]
    [InlineData(".hidden", ".*", true)]
    [InlineData("test.backup.csv", "*.csv", true)]
    [InlineData("test.backup.csv", "test.*.csv", true)]
    public void GlobMatch_ReturnsExpected(string input, string pattern, bool expected)
    {
        GenericFileUtils.GlobMatch(input, pattern).Should().Be(expected);
    }

    [Fact]
    public void GlobMatch_EmptyPattern_MatchesNothing()
    {
        // Empty after split gives no patterns
        GenericFileUtils.GlobMatch("test.csv", "").Should().BeFalse();
    }

    // ── CombinePath ─────────────────────────────────────────────────

    private readonly SftpFileOperations _ops = new(new SftpEndpointOptions
    {
        Host = "test", Username = "test", Password = "test"
    });

    [Theory]
    [InlineData("/upload", "report.csv", "/upload/report.csv")]
    [InlineData("/upload/", "report.csv", "/upload/report.csv")]
    [InlineData("/upload", "/report.csv", "/upload/report.csv")]
    [InlineData("/", "report.csv", "/report.csv")]
    [InlineData("", "report.csv", "/report.csv")]
    [InlineData("/upload", "", "/upload")]
    [InlineData("/upload/data", "sub/file.txt", "/upload/data/sub/file.txt")]
    public void CombinePath_ReturnsExpected(string basePath, string relativePath, string expected)
    {
        _ops.CombinePath(basePath, relativePath).Should().Be(expected);
    }

    // ── EnsureRemoteDirectoryExists ─────────────────────────────────

    [Fact]
    public void EnsureRemoteDirectoryExists_CalledWithNullClient_Throws()
    {
        var act = () => SftpFileOperations.EnsureRemoteDirectoryExists(null!, "/test");
        act.Should().Throw<NullReferenceException>();
    }
}
