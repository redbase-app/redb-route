using redb.Route.GenericFile;
using redb.Route.Sftp;

namespace redb.Route.Tests.Sftp;

public sealed class SftpEnumsTests
{
    [Fact]
    public void GenericFileExistStrategy_HasExpectedValues()
    {
        Enum.GetNames<GenericFileExistStrategy>().Should()
            .BeEquivalentTo("Override", "Append", "Fail", "Ignore", "Move", "TryRename");
    }

    [Fact]
    public void GenericFileSortBy_HasExpectedValues()
    {
        Enum.GetNames<GenericFileSortBy>().Should()
            .BeEquivalentTo("None", "Name", "NameDesc", "Modified", "ModifiedDesc", "Size", "SizeDesc");
    }

    [Fact]
    public void SftpProxyType_HasExpectedValues()
    {
        Enum.GetNames<SftpProxyType>().Should()
            .BeEquivalentTo("None", "Socks4", "Socks5", "Http");
    }

    [Fact]
    public void SftpMoveExistingStrategy_HasExpectedValues()
    {
        Enum.GetNames<SftpMoveExistingStrategy>().Should()
            .BeEquivalentTo("Backup", "Timestamp", "Guid");
    }

    [Fact]
    public void SftpSeparator_HasExpectedValues()
    {
        Enum.GetNames<SftpSeparator>().Should()
            .BeEquivalentTo("Auto", "Unix", "Windows");
    }

    [Theory]
    [InlineData(GenericFileExistStrategy.Override, 0)]
    [InlineData(GenericFileExistStrategy.Append, 1)]
    [InlineData(GenericFileExistStrategy.Fail, 2)]
    [InlineData(GenericFileExistStrategy.Ignore, 3)]
    [InlineData(GenericFileExistStrategy.Move, 4)]
    [InlineData(GenericFileExistStrategy.TryRename, 5)]
    public void GenericFileExistStrategy_IntValues_AreStable(GenericFileExistStrategy strategy, int expected)
    {
        ((int)strategy).Should().Be(expected);
    }

    [Theory]
    [InlineData(GenericFileSortBy.None, 0)]
    [InlineData(GenericFileSortBy.Name, 1)]
    [InlineData(GenericFileSortBy.NameDesc, 2)]
    [InlineData(GenericFileSortBy.Modified, 3)]
    [InlineData(GenericFileSortBy.ModifiedDesc, 4)]
    [InlineData(GenericFileSortBy.Size, 5)]
    [InlineData(GenericFileSortBy.SizeDesc, 6)]
    public void GenericFileSortBy_IntValues_AreStable(GenericFileSortBy sort, int expected)
    {
        ((int)sort).Should().Be(expected);
    }
}
