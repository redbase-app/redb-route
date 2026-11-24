using Microsoft.Extensions.Logging;
using redb.Route.Core;
using redb.Route.S3;
using redb.Route.Tests.Cluster;

namespace redb.Route.Tests.S3;

/// <summary>
/// The "client created" log names the region and service URL the client talks to: a connection factory's, read from the
/// client's own configuration, not the endpoint's defaults.
/// </summary>
public sealed class S3ClientLogTests
{
    [Fact]
    public async Task ClientCreated_WithAConnectionFactory_LogsTheFactorysTarget()
    {
        var capture = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Debug));
        await using var context = new RouteContext(loggerFactory: loggerFactory);
        context.AddComponent(new S3Component());
        context.AddToRegistry("minio", new S3ConnectionFactory
        {
            ServiceUrl = "http://minio.internal:9000", Region = "eu-west-3", AccessKey = "ak", SecretKey = "sk", ForcePathStyle = true,
        });
        var endpoint = (S3Endpoint)context.GetEndpoint("s3://invoices?connectionFactory=minio");

        await endpoint.GetOrCreateClientAsync();

        var created = capture.Entries.Single(e => e.Message.StartsWith("S3 client created", StringComparison.Ordinal)).Message;
        created.Should().Contain("minio.internal:9000").And.Contain("eu-west-3").And.Contain("minio");
        created.Should().NotContain("us-east-1", "that is the endpoint's default region, not the client's");
    }
}
