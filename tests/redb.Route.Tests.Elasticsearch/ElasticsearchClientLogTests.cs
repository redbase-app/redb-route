using Microsoft.Extensions.Logging;
using redb.Route.Core;
using redb.Route.Elasticsearch;
using redb.Route.Tests.Cluster;

namespace redb.Route.Tests.Elasticsearch;

/// <summary>
/// The "client created" log names the nodes the client was built with: a connection factory's, not the endpoint's
/// default <c>Nodes</c> (http://localhost:9200), which a user then took for the address in use.
/// </summary>
public sealed class ElasticsearchClientLogTests
{
    [Fact]
    public async Task ClientCreated_WithAConnectionFactory_LogsTheFactorysNodes()
    {
        var capture = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Debug));
        await using var context = new RouteContext(loggerFactory: loggerFactory);
        context.AddComponent(new ElasticsearchComponent());
        context.AddToRegistry("cluster", new ElasticsearchConnectionFactory { Nodes = "http://es-a.internal:9201,http://es-b.internal:9201" });
        var endpoint = (ElasticsearchEndpoint)context.GetEndpoint("elasticsearch://orders?connectionFactory=cluster");

        await endpoint.GetOrCreateClientAsync();

        var created = capture.Entries.Single(e => e.Message.StartsWith("Elasticsearch client created", StringComparison.Ordinal)).Message;
        created.Should().Contain("http://es-a.internal:9201").And.Contain("http://es-b.internal:9201").And.Contain("cluster");
        created.Should().NotContain("localhost:9200", "that is the endpoint's default, not what the client connects to");
    }

    [Fact]
    public async Task ClientCreated_FromTheUri_LogsTheUrisNodes_WithoutCredentials()
    {
        var capture = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Debug));
        await using var context = new RouteContext(loggerFactory: loggerFactory);
        context.AddComponent(new ElasticsearchComponent());
        var endpoint = (ElasticsearchEndpoint)context.GetEndpoint("elasticsearch://orders?nodes=http://user:secret@es-c.internal:9202");

        await endpoint.GetOrCreateClientAsync();

        var created = capture.Entries.Single(e => e.Message.StartsWith("Elasticsearch client created", StringComparison.Ordinal)).Message;
        created.Should().Contain("es-c.internal:9202").And.NotContain("secret");
    }
}
