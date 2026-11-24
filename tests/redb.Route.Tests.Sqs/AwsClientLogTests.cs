using Microsoft.Extensions.Logging;
using redb.Route.Core;
using redb.Route.Sqs;
using redb.Route.Tests.Cluster;

namespace redb.Route.Tests.Sqs;

/// <summary>
/// The SQS and SNS "client created" logs name the region and service URL the client talks to: a connection factory's,
/// read from the client's own configuration, not the endpoint's defaults.
/// </summary>
public sealed class AwsClientLogTests
{
    private static AwsConnectionFactory Factory() => new()
    {
        ServiceUrl = "http://localstack.internal:4566", Region = "eu-north-1", AccessKey = "ak", SecretKey = "sk",
    };

    [Fact]
    public async Task SqsClientCreated_WithAConnectionFactory_LogsTheFactorysTarget()
    {
        var capture = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Debug));
        await using var context = new RouteContext(loggerFactory: loggerFactory);
        context.AddComponent(new SqsComponent());
        context.AddToRegistry("aws", Factory());
        var endpoint = (SqsEndpoint)context.GetEndpoint("sqs://orders?connectionFactory=aws");

        await endpoint.GetOrCreateClientAsync();

        var created = capture.Entries.Single(e => e.Message.StartsWith("SQS client created", StringComparison.Ordinal)).Message;
        created.Should().Contain("localstack.internal:4566").And.Contain("eu-north-1").And.Contain("aws");
        created.Should().NotContain("(default)");
    }

    [Fact]
    public async Task SnsClientCreated_WithAConnectionFactory_LogsTheFactorysTarget()
    {
        var capture = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Debug));
        await using var context = new RouteContext(loggerFactory: loggerFactory);
        context.AddComponent(new SnsComponent());
        context.AddToRegistry("aws", Factory());
        var endpoint = (SnsEndpoint)context.GetEndpoint("sns://alerts?connectionFactory=aws");

        await endpoint.GetOrCreateClientAsync();

        var created = capture.Entries.Single(e => e.Message.StartsWith("SNS client created", StringComparison.Ordinal)).Message;
        created.Should().Contain("localstack.internal:4566").And.Contain("eu-north-1").And.Contain("aws");
        created.Should().NotContain("(default)");
    }
}
