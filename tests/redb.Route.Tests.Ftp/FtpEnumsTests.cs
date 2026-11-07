using redb.Route.GenericFile;
using redb.Route.Ftp;

namespace redb.Route.Tests.Ftp;

public sealed class FtpEnumsTests
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
    public void FtpTransferType_HasExpectedValues()
    {
        Enum.GetNames<FtpTransferType>().Should()
            .BeEquivalentTo("Binary", "Ascii");
    }

    [Fact]
    public void FtpMoveExistingStrategy_HasExpectedValues()
    {
        Enum.GetNames<FtpMoveExistingStrategy>().Should()
            .BeEquivalentTo("Backup", "Timestamp", "Guid");
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

    [Theory]
    [InlineData(FtpTransferType.Binary, 0)]
    [InlineData(FtpTransferType.Ascii, 1)]
    public void FtpTransferType_IntValues_AreStable(FtpTransferType type, int expected)
    {
        ((int)type).Should().Be(expected);
    }

    [Theory]
    [InlineData(FtpMoveExistingStrategy.Backup, 0)]
    [InlineData(FtpMoveExistingStrategy.Timestamp, 1)]
    [InlineData(FtpMoveExistingStrategy.Guid, 2)]
    public void FtpMoveExistingStrategy_IntValues_AreStable(FtpMoveExistingStrategy strategy, int expected)
    {
        ((int)strategy).Should().Be(expected);
    }
}
