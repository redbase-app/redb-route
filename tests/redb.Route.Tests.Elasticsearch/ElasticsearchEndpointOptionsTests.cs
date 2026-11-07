using redb.Route.Elasticsearch;

namespace redb.Route.Tests.Elasticsearch;

public sealed class ElasticsearchEndpointOptionsTests
{
    // ═══════════════════════════════════════════════════════════════════
    //  VALIDATE
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Validate_MissingNodes_Throws()
    {
        var opts = new ElasticsearchEndpointOptions { Nodes = "", ConnectionFactory = "" };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*Nodes*");
    }

    [Fact]
    public void Validate_ConnectionFactory_AllowsMissingNodes()
    {
        var opts = new ElasticsearchEndpointOptions { Nodes = "", ConnectionFactory = "myFactory" };
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_WithNodes_DoesNotThrow()
    {
        var opts = new ElasticsearchEndpointOptions { Nodes = "http://localhost:9200" };
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_DelayTooSmall_Throws()
    {
        var opts = new ElasticsearchEndpointOptions { Delay = 50 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*Delay*");
    }

    [Fact]
    public void Validate_SizeZero_Throws()
    {
        var opts = new ElasticsearchEndpointOptions { Size = 0 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*Size*");
    }

    [Fact]
    public void Validate_SizeTooLarge_Throws()
    {
        var opts = new ElasticsearchEndpointOptions { Size = 10_001 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*Size*");
    }

    [Fact]
    public void Validate_BulkSizeZero_Throws()
    {
        var opts = new ElasticsearchEndpointOptions { BulkSize = 0 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*BulkSize*");
    }

    [Fact]
    public void Validate_RequestTimeoutTooSmall_Throws()
    {
        var opts = new ElasticsearchEndpointOptions { RequestTimeout = 500 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*RequestTimeout*");
    }

    [Fact]
    public void Validate_MaxRetriesNegative_Throws()
    {
        var opts = new ElasticsearchEndpointOptions { MaxRetries = -1 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*MaxRetries*");
    }

    [Fact]
    public void Validate_ValidOptions_DoesNotThrow()
    {
        var opts = new ElasticsearchEndpointOptions
        {
            Nodes = "http://localhost:9200",
            Delay = 1000,
            Size = 50,
            BulkSize = 200,
            RequestTimeout = 5000,
            MaxRetries = 5,
        };
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  BIND FROM URI
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void BindFromUri_AllProperties_Bound()
    {
        var opts = new ElasticsearchEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string>
        {
            ["nodes"] = "http://node1:9200,http://node2:9200",
            ["apiKey"] = "base64key",
            ["username"] = "elastic",
            ["password"] = "secret",
            ["certificateFingerprint"] = "abc123",
            ["connectionFactory"] = "myFactory",
            ["enableDebugMode"] = "true",
            ["requestTimeout"] = "60000",
            ["pingTimeout"] = "5000",
            ["deadTimeout"] = "120000",
            ["maxDeadTimeout"] = "900000",
            ["maxRetries"] = "5",
            ["operation"] = "Search",
            ["pipeline"] = "my-pipeline",
            ["routing"] = "shard-1",
            ["refresh"] = "wait_for",
            ["bulkSize"] = "500",
            ["delay"] = "10000",
            ["initialDelay"] = "2000",
            ["query"] = "{\"match_all\":{}}",
            ["size"] = "50",
            ["scrollTimeout"] = "2m",
            ["sort"] = "timestamp:desc",
            ["deleteAfterRead"] = "true",
            ["trackTotalHits"] = "false",
            ["sourceIncludes"] = "title,author",
            ["sourceExcludes"] = "large_blob",
        });

        opts.Nodes.Should().Be("http://node1:9200,http://node2:9200");
        opts.ApiKey.Should().Be("base64key");
        opts.Username.Should().Be("elastic");
        opts.Password.Should().Be("secret");
        opts.CertificateFingerprint.Should().Be("abc123");
        opts.ConnectionFactory.Should().Be("myFactory");
        opts.EnableDebugMode.Should().BeTrue();
        opts.RequestTimeout.Should().Be(60_000);
        opts.PingTimeout.Should().Be(5000);
        opts.DeadTimeout.Should().Be(120_000);
        opts.MaxDeadTimeout.Should().Be(900_000);
        opts.MaxRetries.Should().Be(5);
        opts.Operation.Should().Be(ElasticsearchOperationType.Search);
        opts.Pipeline.Should().Be("my-pipeline");
        opts.Routing.Should().Be("shard-1");
        opts.Refresh.Should().Be("wait_for");
        opts.BulkSize.Should().Be(500);
        opts.Delay.Should().Be(10_000);
        opts.InitialDelay.Should().Be(2000);
        opts.Query.Should().Be("{\"match_all\":{}}");
        opts.Size.Should().Be(50);
        opts.ScrollTimeout.Should().Be("2m");
        opts.Sort.Should().Be("timestamp:desc");
        opts.DeleteAfterRead.Should().BeTrue();
        opts.TrackTotalHits.Should().BeFalse();
        opts.SourceIncludes.Should().Be("title,author");
        opts.SourceExcludes.Should().Be("large_blob");
    }

    [Fact]
    public void BindFromUri_OperationType_Parsed()
    {
        var opts = new ElasticsearchEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string> { ["operation"] = "Bulk" });
        opts.Operation.Should().Be(ElasticsearchOperationType.Bulk);
    }

    [Fact]
    public void Defaults_AreCorrect()
    {
        var opts = new ElasticsearchEndpointOptions();
        opts.Nodes.Should().Be("http://localhost:9200");
        opts.Delay.Should().Be(5000);
        opts.InitialDelay.Should().Be(1000);
        opts.Size.Should().Be(100);
        opts.BulkSize.Should().Be(100);
        opts.Operation.Should().Be(ElasticsearchOperationType.Index);
        opts.RequestTimeout.Should().Be(30_000);
        opts.PingTimeout.Should().Be(2000);
        opts.DeadTimeout.Should().Be(60_000);
        opts.MaxDeadTimeout.Should().Be(600_000);
        opts.MaxRetries.Should().Be(3);
        opts.DeleteAfterRead.Should().BeFalse();
        opts.TrackTotalHits.Should().BeTrue();
    }
}
