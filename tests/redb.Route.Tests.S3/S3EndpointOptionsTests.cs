using redb.Route.Core;
using redb.Route.S3;

namespace redb.Route.Tests.S3;

public sealed class S3EndpointOptionsTests
{
    [Fact]
    public void Validate_NoCredentials_Throws()
    {
        var opts = new S3EndpointOptions { AccessKey = "", SecretKey = "" };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*credentials*");
    }

    [Fact]
    public void Validate_WithAccessKeyAndSecretKey_DoesNotThrow()
    {
        var opts = new S3EndpointOptions { AccessKey = "key", SecretKey = "secret" };
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_DefaultCredentialsProvider_DoesNotThrow()
    {
        var opts = new S3EndpointOptions { UseDefaultCredentialsProvider = true };
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_ProfileName_DoesNotThrow()
    {
        var opts = new S3EndpointOptions { ProfileName = "default" };
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_ConnectionFactory_DoesNotThrow()
    {
        var opts = new S3EndpointOptions { ConnectionFactory = "myFactory" };
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_MoveAfterRead_WithoutDestinationBucket_Throws()
    {
        var opts = new S3EndpointOptions
        {
            AccessKey = "key", SecretKey = "secret",
            MoveAfterRead = true, DeleteAfterRead = false, DestinationBucket = ""
        };
        var act = () => opts.Validate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*DestinationBucket*");
    }

    [Fact]
    public void Validate_MoveAndDeleteAfterRead_Throws()
    {
        var opts = new S3EndpointOptions
        {
            AccessKey = "key", SecretKey = "secret",
            MoveAfterRead = true, DeleteAfterRead = true, DestinationBucket = "dest"
        };
        var act = () => opts.Validate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*MoveAfterRead*DeleteAfterRead*");
    }

    [Fact]
    public void Validate_NegativeDelay_Throws()
    {
        var opts = new S3EndpointOptions { AccessKey = "key", SecretKey = "secret", Delay = -1 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_PartSizeTooSmall_Throws()
    {
        var opts = new S3EndpointOptions { AccessKey = "key", SecretKey = "secret", PartSize = 1000 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*5 MB*");
    }

    [Fact]
    public void Validate_KmsWithoutKeyId_Throws()
    {
        var opts = new S3EndpointOptions
        {
            AccessKey = "key", SecretKey = "secret",
            ServerSideEncryption = S3ServerSideEncryption.AwsKms, KmsKeyId = ""
        };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*KmsKeyId*");
    }

    [Fact]
    public void Validate_CustomerKeyWithoutKey_Throws()
    {
        var opts = new S3EndpointOptions
        {
            AccessKey = "key", SecretKey = "secret",
            ServerSideEncryption = S3ServerSideEncryption.CustomerKey, CustomerKeyId = ""
        };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*CustomerKeyId*");
    }

    [Fact]
    public void Validate_InvalidProxyPort_Throws()
    {
        var opts = new S3EndpointOptions { AccessKey = "key", SecretKey = "secret", ProxyPort = 99999 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_StreamingUploadZeroBatch_Throws()
    {
        var opts = new S3EndpointOptions
        {
            AccessKey = "key", SecretKey = "secret",
            StreamingUploadMode = true, BatchMessageNumber = 0
        };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void BindFromUri_ParsesAllCoreProperties()
    {
        var opts = new S3EndpointOptions();
        opts.BindFromUri(new Dictionary<string, string>
        {
            ["serviceUrl"] = "http://localhost:9000",
            ["region"] = "eu-west-1",
            ["accessKey"] = "AK",
            ["secretKey"] = "SK",
            ["forcePathStyle"] = "true",
            ["delay"] = "5000",
            ["maxMessagesPerPoll"] = "20",
            ["deleteAfterRead"] = "false",
            ["prefix"] = "data/",
            ["include"] = "*.csv",
            ["sortBy"] = "LastModified",
            ["autoCreateBucket"] = "true",
            ["multiPartUpload"] = "true",
            ["partSize"] = "10485760",
            ["storageClass"] = "GLACIER",
            ["presignedUrlExpiration"] = "7200000",
        });

        opts.ServiceUrl.Should().Be("http://localhost:9000");
        opts.Region.Should().Be("eu-west-1");
        opts.AccessKey.Should().Be("AK");
        opts.SecretKey.Should().Be("SK");
        opts.ForcePathStyle.Should().BeTrue();
        opts.Delay.Should().Be(5000);
        opts.MaxMessagesPerPoll.Should().Be(20);
        opts.DeleteAfterRead.Should().BeFalse();
        opts.Prefix.Should().Be("data/");
        opts.Include.Should().Be("*.csv");
        opts.SortBy.Should().Be(S3SortBy.LastModified);
        opts.AutoCreateBucket.Should().BeTrue();
        opts.MultiPartUpload.Should().BeTrue();
        opts.PartSize.Should().Be(10_485_760);
        opts.StorageClass.Should().Be("GLACIER");
        opts.PresignedUrlExpiration.Should().Be(7_200_000);
    }

    [Fact]
    public void Defaults_AreCorrect()
    {
        var opts = new S3EndpointOptions();
        opts.Region.Should().Be("us-east-1");
        opts.Delay.Should().Be(60_000);
        opts.InitialDelay.Should().Be(1000);
        opts.MaxMessagesPerPoll.Should().Be(10);
        opts.DeleteAfterRead.Should().BeTrue();
        opts.IncludeBody.Should().BeTrue();
        opts.PartSize.Should().Be(26_214_400);
        opts.PresignedUrlExpiration.Should().Be(3_600_000);
        opts.CannedAcl.Should().Be(S3CannedAcl.Private);
        opts.RetryCount.Should().Be(3);
        opts.MaxConnections.Should().Be(50);
    }
}
