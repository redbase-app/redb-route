using redb.Route.Core;
using redb.Route.S3;
using S3Dsl = redb.Route.S3.S3;

namespace redb.Route.Tests.S3;

public sealed class S3DslTests
{
    [Fact]
    public void Bucket_GeneratesBasicUri()
    {
        string uri = S3Dsl.Bucket("my-bucket");
        uri.Should().Be("s3://my-bucket");
    }

    [Fact]
    public void Bucket_WithOperation_GeneratesOperationUri()
    {
        string uri = S3Dsl.Bucket("my-bucket", S3OperationType.CopyObject);
        uri.Should().Be("s3://CopyObject:my-bucket");
    }

    [Fact]
    public void Builder_MinIOConfig_GeneratesFullUri()
    {
        string uri = S3Dsl.Bucket("test-bucket")
            .ServiceUrl("http://localhost:9000")
            .ForcePathStyle()
            .AccessKey("minioadmin")
            .SecretKey("minioadmin")
            .Region("us-east-1");

        uri.Should().Contain("s3://test-bucket?");
        uri.Should().Contain("serviceUrl=http");
        uri.Should().Contain("forcePathStyle=true");
        uri.Should().Contain("accessKey=minioadmin");
        uri.Should().Contain("secretKey=minioadmin");
        uri.Should().Contain("region=us-east-1");
    }

    [Fact]
    public void Builder_ConsumerOptions_GeneratesUri()
    {
        string uri = S3Dsl.Bucket("data")
            .AccessKey("key").SecretKey("secret")
            .Prefix("incoming/")
            .Include("*.csv")
            .Exclude("*.tmp")
            .Delay(5000)
            .MaxMessagesPerPoll(50)
            .DeleteAfterRead()
            .SortBy(S3SortBy.LastModified)
            .MinAge(10_000)
            .Idempotent();

        uri.Should().Contain("prefix=incoming");
        uri.Should().Contain("include=*.csv");
        uri.Should().Contain("exclude=*.tmp");
        uri.Should().Contain("delay=5000");
        uri.Should().Contain("maxMessagesPerPoll=50");
        uri.Should().Contain("deleteAfterRead=true");
        uri.Should().Contain("sortBy=LastModified");
        uri.Should().Contain("minAge=10000");
        uri.Should().Contain("idempotent=true");
    }

    [Fact]
    public void Builder_MoveAfterRead_GeneratesUri()
    {
        string uri = S3Dsl.Bucket("source")
            .AccessKey("key").SecretKey("secret")
            .MoveAfterRead("archive-bucket", prefix: "processed/", suffix: "-done")
            .RemovePrefixOnMove();

        uri.Should().Contain("moveAfterRead=true");
        uri.Should().Contain("destinationBucket=archive-bucket");
        uri.Should().Contain("destinationBucketPrefix=processed");
        uri.Should().Contain("destinationBucketSuffix=-done");
        uri.Should().Contain("removePrefixOnMove=true");
    }

    [Fact]
    public void Builder_ProducerOptions_GeneratesUri()
    {
        string uri = S3Dsl.Bucket("uploads")
            .AccessKey("key").SecretKey("secret")
            .KeyName("data/${header.fileName}")
            .StorageClass("INTELLIGENT_TIERING")
            .ContentType("application/json")
            .MultiPartUpload()
            .PartSize(10_485_760)
            .CannedAcl(S3CannedAcl.PublicRead);

        uri.Should().Contain("keyName=data");
        uri.Should().Contain("storageClass=INTELLIGENT_TIERING");
        uri.Should().Contain("contentType=application%2fjson");
        uri.Should().Contain("multiPartUpload=true");
        uri.Should().Contain("partSize=10485760");
        uri.Should().Contain("cannedAcl=PublicRead");
    }

    [Fact]
    public void Builder_Encryption_GeneratesUri()
    {
        string uri = S3Dsl.Bucket("secure")
            .AccessKey("key").SecretKey("secret")
            .UseKmsEncryption("my-kms-key-id");

        uri.Should().Contain("serverSideEncryption=AwsKms");
        uri.Should().Contain("kmsKeyId=my-kms-key-id");
    }

    [Fact]
    public void Builder_StreamingUpload_GeneratesUri()
    {
        string uri = S3Dsl.Bucket("stream")
            .AccessKey("key").SecretKey("secret")
            .StreamingUpload(batchMessages: 20, batchSize: 2_097_152)
            .NamingStrategy(S3NamingStrategy.Random);

        uri.Should().Contain("streamingUploadMode=true");
        uri.Should().Contain("batchMessageNumber=20");
        uri.Should().Contain("batchSize=2097152");
        uri.Should().Contain("namingStrategy=Random");
    }

    [Fact]
    public void Builder_ImplicitStringConversion()
    {
        S3Builder builder = S3Dsl.Bucket("test").AccessKey("key").SecretKey("secret");
        string uri = builder;
        uri.Should().StartWith("s3://test?");
    }

    [Fact]
    public void Builder_ToString_EqualsImplicit()
    {
        var builder = S3Dsl.Bucket("test").AccessKey("key").SecretKey("secret");
        builder.ToString().Should().Be((string)builder);
    }

    [Fact]
    public void Builder_StreamBody_SetsParam()
    {
        string uri = S3Dsl.Bucket("data")
            .AccessKey("key").SecretKey("secret")
            .StreamBody();
        uri.Should().Contain("streamBody=true");
    }

    [Fact]
    public void Builder_StreamBody_RoundTrip()
    {
        string uri = S3Dsl.Bucket("data")
            .AccessKey("key").SecretKey("secret")
            .StreamBody()
            .Build();
        var parsed = EndpointUriParser.Parse(uri);
        parsed.RawParameters["streamBody"].Should().Be("true");
    }
}
