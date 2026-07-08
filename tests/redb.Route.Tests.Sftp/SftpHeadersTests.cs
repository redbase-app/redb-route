using redb.Route.Sftp;

namespace redb.Route.Tests.Sftp;

public sealed class SftpHeadersTests
{
    [Fact]
    public void AllHeaders_HaveCorrectPrefix()
    {
        var headerFields = typeof(SftpHeaders)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string) && f.Name != nameof(SftpHeaders.Prefix))
            .ToList();

        headerFields.Should().NotBeEmpty();

        foreach (var field in headerFields)
        {
            var val = (string)field.GetValue(null)!;
            val.Should().StartWith(SftpHeaders.Prefix,
                $"header {field.Name} should start with '{SftpHeaders.Prefix}'");
        }
    }

    [Fact]
    public void Prefix_IsCorrect()
    {
        SftpHeaders.Prefix.Should().Be("redbSftp.");
    }

    [Fact]
    public void AllHeaders_AreUnique()
    {
        var headerFields = typeof(SftpHeaders)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string))
            .Select(f => (string)f.GetValue(null)!)
            .ToList();

        headerFields.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void KnownConsumerHeaders_ExistAndCorrect()
    {
        SftpHeaders.FileName.Should().Be("redbSftp.Name");
        SftpHeaders.FileNameOnly.Should().Be("redbSftp.NameOnly");
        SftpHeaders.FileExtension.Should().Be("redbSftp.Extension");
        SftpHeaders.RemotePath.Should().Be("redbSftp.RemotePath");
        SftpHeaders.RelativePath.Should().Be("redbSftp.RelativePath");
        SftpHeaders.RemoteParent.Should().Be("redbSftp.RemoteParent");
        SftpHeaders.FileLength.Should().Be("redbSftp.Length");
        SftpHeaders.FileLastModified.Should().Be("redbSftp.LastModified");
        SftpHeaders.FilePermissions.Should().Be("redbSftp.Permissions");
        SftpHeaders.FileOwner.Should().Be("redbSftp.Owner");
        SftpHeaders.FileGroup.Should().Be("redbSftp.Group");
        SftpHeaders.Host.Should().Be("redbSftp.Host");
        SftpHeaders.Port.Should().Be("redbSftp.Port");
        SftpHeaders.Username.Should().Be("redbSftp.Username");
    }

    [Fact]
    public void KnownProducerHeaders_ExistAndCorrect()
    {
        SftpHeaders.FileNameProduced.Should().Be("redbSftp.NameProduced");
    }

    [Fact]
    public void HeaderCount_IsExpected()
    {
        var count = typeof(SftpHeaders)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Count(f => f.FieldType == typeof(string));

        // Prefix + 15 consumer/producer headers = 16
        count.Should().Be(16);
    }
}
