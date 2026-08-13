using redb.Route.Firebase;

namespace redb.Route.Tests.Firebase;

public sealed class FirebaseStorageDslTests
{
    [Fact]
    public void Bucket_GeneratesBasicUri()
    {
        string uri = FirebaseStorage.Bucket("my-app.appspot.com");
        uri.Should().Be("fbstorage://my-app.appspot.com");
    }

    [Fact]
    public void Bucket_WithPrefix_GeneratesUri()
    {
        string uri = FirebaseStorage.Bucket("my-app.appspot.com", "uploads/");
        uri.Should().Be("fbstorage://my-app.appspot.com/uploads/");
    }

    [Fact]
    public void Builder_ProducerOptions_GeneratesUri()
    {
        string uri = FirebaseStorage.Bucket("bucket")
            .Operation(FirebaseStorageOperationType.Upload)
            .ObjectName("data/report.csv")
            .ContentType("text/csv")
            .CacheControl("public, max-age=3600")
            .StreamBody()
            .CredentialPath("/secrets/sa.json")
            .ProjectId("proj")
            .ConnectionFactory("cf");

        uri.Should().Contain("fbstorage://bucket?");
        uri.Should().Contain("operation=Upload");
        uri.Should().Contain("objectName=data");
        uri.Should().Contain("contentType=");
        uri.Should().Contain("cacheControl=");
        uri.Should().Contain("streamBody=True");
        uri.Should().Contain("credentialPath=");
        uri.Should().Contain("projectId=proj");
        uri.Should().Contain("connectionFactory=cf");
    }

    [Fact]
    public void Builder_ConsumerOptions_GeneratesUri()
    {
        string uri = FirebaseStorage.Bucket("data")
            .Prefix("incoming/")
            .Include("*.csv")
            .Exclude("*.tmp")
            .Delay(10000)
            .MaxMessagesPerPoll(20)
            .DeleteAfterRead()
            .Idempotent()
            .IncludeBody();

        uri.Should().Contain("prefix=incoming");
        uri.Should().Contain("include=%2A.csv");
        uri.Should().Contain("exclude=%2A.tmp");
        uri.Should().Contain("delay=10000");
        uri.Should().Contain("maxMessagesPerPoll=20");
        uri.Should().Contain("deleteAfterRead=True");
        uri.Should().Contain("idempotent=True");
        uri.Should().Contain("includeBody=True");
    }

    [Fact]
    public void Builder_MoveAfterRead_GeneratesUri()
    {
        string uri = FirebaseStorage.Bucket("source")
            .MoveAfterRead("processed/");
        uri.Should().Contain("moveAfterRead=processed");
    }

    [Fact]
    public void Builder_ImplicitStringConversion()
    {
        FirebaseStorageBuilder builder = FirebaseStorage.Bucket("test");
        string uri = builder;
        uri.Should().Be("fbstorage://test");
    }

    [Fact]
    public void Builder_ToString_EqualsImplicit()
    {
        var builder = FirebaseStorage.Bucket("test").Delay(5000);
        builder.ToString().Should().Be((string)builder);
    }

    [Fact]
    public void Builder_NoParams_NoQueryString()
    {
        string uri = FirebaseStorage.Bucket("data");
        uri.Should().NotContain("?");
    }
}
