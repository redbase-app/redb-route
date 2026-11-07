using redb.Route.Ftp;

namespace redb.Route.Tests.Ftp;

public sealed class FtpHeadersTests
{
    [Fact]
    public void AllHeaders_HaveCorrectPrefix()
    {
        var headerFields = typeof(FtpHeaders)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string) && f.Name != nameof(FtpHeaders.Prefix))
            .ToList();

        headerFields.Should().NotBeEmpty();

        foreach (var field in headerFields)
        {
            var val = (string)field.GetValue(null)!;
            val.Should().StartWith(FtpHeaders.Prefix,
                $"header {field.Name} should start with '{FtpHeaders.Prefix}'");
        }
    }

    [Fact]
    public void Prefix_IsCorrect()
    {
        FtpHeaders.Prefix.Should().Be("redbFtp.");
    }

    [Fact]
    public void AllHeaders_AreUnique()
    {
        var headerFields = typeof(FtpHeaders)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string))
            .Select(f => (string)f.GetValue(null)!)
            .ToList();

        headerFields.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void KnownConsumerHeaders_ExistAndCorrect()
    {
        FtpHeaders.FileName.Should().Be("redbFtp.Name");
        FtpHeaders.FileNameOnly.Should().Be("redbFtp.NameOnly");
        FtpHeaders.FileExtension.Should().Be("redbFtp.Extension");
        FtpHeaders.RemotePath.Should().Be("redbFtp.RemotePath");
        FtpHeaders.RelativePath.Should().Be("redbFtp.RelativePath");
        FtpHeaders.RemoteParent.Should().Be("redbFtp.RemoteParent");
        FtpHeaders.FileLength.Should().Be("redbFtp.Length");
        FtpHeaders.FileLastModified.Should().Be("redbFtp.LastModified");
        FtpHeaders.Host.Should().Be("redbFtp.Host");
        FtpHeaders.Port.Should().Be("redbFtp.Port");
        FtpHeaders.Username.Should().Be("redbFtp.Username");
    }

    [Fact]
    public void ProducerHeaders_ExistAndCorrect()
    {
        FtpHeaders.FileNameProduced.Should().Be("redbFtp.NameProduced");
    }
}
