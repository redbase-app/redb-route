using redb.Route.Core;
using redb.Route.Elasticsearch;
using EsDsl = redb.Route.Elasticsearch.Es;

namespace redb.Route.Tests.Elasticsearch;

public sealed class ElasticsearchDslTests
{
    [Fact]
    public void Index_GeneratesBasicUri()
    {
        string uri = EsDsl.Index("my-index");
        uri.Should().Be("elasticsearch://my-index");
    }

    [Fact]
    public void Index_WithOperation_GeneratesOperationUri()
    {
        string uri = EsDsl.Index("my-index", ElasticsearchOperationType.Search);
        uri.Should().Be("elasticsearch://Search:my-index");
    }

    [Fact]
    public void BuildShort_GeneratesEsScheme()
    {
        string uri = EsDsl.Index("my-index").BuildShort();
        uri.Should().Be("es://my-index");
    }

    [Fact]
    public void Builder_FullConfig_GeneratesCompleteUri()
    {
        string uri = EsDsl.Index("my-index")
            .Nodes("http://localhost:9200")
            .ApiKey("base64key")
            .Refresh("wait_for")
            .Pipeline("my-pipeline")
            .Routing("shard-1");

        uri.Should().Contain("elasticsearch://my-index?");
        uri.Should().Contain("nodes=http");
        uri.Should().Contain("apiKey=base64key");
        uri.Should().Contain("refresh=wait_for");
        uri.Should().Contain("pipeline=my-pipeline");
        uri.Should().Contain("routing=shard-1");
    }

    [Fact]
    public void Builder_ConsumerOptions_GeneratesUri()
    {
        string uri = EsDsl.Index("logs")
            .Nodes("http://localhost:9200")
            .Query("{\"match_all\":{}}")
            .Sort("timestamp:desc")
            .Size(50)
            .Delay(10_000)
            .InitialDelay(2000)
            .DeleteAfterRead()
            .TrackTotalHits()
            .SourceIncludes("title,author")
            .SourceExcludes("large_blob");

        uri.Should().Contain("query=%7B");
        uri.Should().Contain("sort=timestamp%3Adesc");
        uri.Should().Contain("size=50");
        uri.Should().Contain("delay=10000");
        uri.Should().Contain("initialDelay=2000");
        uri.Should().Contain("deleteAfterRead=true");
        uri.Should().Contain("trackTotalHits=true");
        uri.Should().Contain("sourceIncludes=title%2Cauthor");
        uri.Should().Contain("sourceExcludes=large_blob");
    }

    [Fact]
    public void Builder_BulkOptions_GeneratesUri()
    {
        string uri = EsDsl.Index("data", ElasticsearchOperationType.Bulk)
            .Nodes("http://localhost:9200")
            .BulkSize(500)
            .Refresh("wait_for");

        uri.Should().Contain("elasticsearch://Bulk:data?");
        uri.Should().Contain("bulkSize=500");
        uri.Should().Contain("refresh=wait_for");
    }

    [Fact]
    public void Builder_ConnectionOptions_GeneratesUri()
    {
        string uri = EsDsl.Index("secure")
            .Nodes("https://node1:9200,https://node2:9200")
            .Username("elastic")
            .Password("secret")
            .CertificateFingerprint("abc123")
            .RequestTimeout(60_000)
            .PingTimeout(5000)
            .MaxRetries(5)
            .DebugMode();

        uri.Should().Contain("username=elastic");
        uri.Should().Contain("password=secret");
        uri.Should().Contain("certificateFingerprint=abc123");
        uri.Should().Contain("requestTimeout=60000");
        uri.Should().Contain("pingTimeout=5000");
        uri.Should().Contain("maxRetries=5");
        uri.Should().Contain("enableDebugMode=true");
    }

    [Fact]
    public void Builder_ConnectionFactory_GeneratesUri()
    {
        string uri = EsDsl.Index("data")
            .ConnectionFactory("myEsFactory");

        uri.Should().Contain("connectionFactory=myEsFactory");
    }

    [Fact]
    public void Builder_ScrollConfig_GeneratesUri()
    {
        string uri = EsDsl.Index("data")
            .Nodes("http://localhost:9200")
            .ScrollTimeout("2m")
            .Size(1000);

        uri.Should().Contain("scrollTimeout=2m");
        uri.Should().Contain("size=1000");
    }

    [Fact]
    public void Builder_ImplicitStringConversion()
    {
        ElasticsearchBuilder builder = EsDsl.Index("test").Nodes("http://localhost:9200");
        string uri = builder;
        uri.Should().StartWith("elasticsearch://test?");
    }

    [Fact]
    public void Builder_ToString_EqualsImplicit()
    {
        var builder = EsDsl.Index("test").Nodes("http://localhost:9200");
        builder.ToString().Should().Be((string)builder);
    }

    [Fact]
    public void Builder_DocumentId_SetsParam()
    {
        string uri = EsDsl.Index("data")
            .Nodes("http://localhost:9200")
            .DocumentId("${header.id}");
        uri.Should().Contain("documentId=");
    }

    [Fact]
    public void Builder_TimeoutOptions_GeneratesUri()
    {
        string uri = EsDsl.Index("data")
            .Nodes("http://localhost:9200")
            .DeadTimeout(120_000)
            .MaxDeadTimeout(900_000);

        uri.Should().Contain("deadTimeout=120000");
        uri.Should().Contain("maxDeadTimeout=900000");
    }

    [Fact]
    public void Index_NullIndexName_Throws()
    {
        var act = () => EsDsl.Index(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Index_EmptyIndexName_Throws()
    {
        var act = () => EsDsl.Index("");
        act.Should().Throw<ArgumentException>();
    }
}
