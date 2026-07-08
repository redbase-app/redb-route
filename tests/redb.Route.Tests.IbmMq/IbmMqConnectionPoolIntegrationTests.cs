using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.IbmMq;
using Xunit.Abstractions;

namespace redb.Route.Tests.IbmMq;

/// <summary>
/// Integration tests for the <see cref="IbmMqComponent"/> connection pool against a live broker
/// (IBM MQ Developer Edition). Validates the new shared-connection model: identical inline
/// parameters share one underlying <c>MQQueueManager</c>, named factories partition by name,
/// endpoint Stop does not affect the pool, and component DisposeAsync drains the pool.
/// </summary>
[Trait("Category", "Integration")]
[Collection("IbmMqIntegration")]
public sealed class IbmMqConnectionPoolIntegrationTests
{
    private const string Host = "localhost";
    private const int Port = 1414;
    private const string Channel = "DEV.APP.SVRCONN";
    private const string QueueManager = "QM1";
    private const string User = "app";
    private const string Password = "admin";
    private const string Queue = "DEV.QUEUE.1";

    private readonly ITestOutputHelper _output;

    public IbmMqConnectionPoolIntegrationTests(ITestOutputHelper output) => _output = output;

    private static IbmMqEndpoint CreateEndpoint(IbmMqComponent component, string destination, string? extraParams = null)
    {
        var qs = $"host={Host}&port={Port}&channel={Channel}&queueManager={QueueManager}&user={User}&password={Password}";
        if (extraParams is not null) qs += $"&{extraParams}";
        var uri = EndpointUriParser.Parse($"wmq:{destination}?{qs}");
        return (IbmMqEndpoint)component.CreateEndpoint(uri);
    }

    /// <summary>
    /// Drain all messages currently sitting on the given queues so that pool tests do not
    /// leave residue that pollutes other integration tests sharing the same DEV queues.
    /// </summary>
    private static async Task DrainAsync(params string[] queues)
    {
        var component = new IbmMqComponent();
        try
        {
            foreach (var q in queues)
            {
                var ep = CreateEndpoint(component, q, "waitInterval=200");
                var processor = Substitute.For<IProcessor>();
                processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
                    .Returns(Task.CompletedTask);
                var consumer = (IbmMqConsumer)ep.CreateConsumer(processor);
                await consumer.Start();
                // Give the consumer enough cycles to drain whatever is queued.
                await Task.Delay(1_500);
                await consumer.Stop();
                await ep.Stop();
            }
        }
        finally
        {
            await component.DisposeAsync();
        }
    }

    // NOTE: As of the dynamic-endpoints / dedicated-connections refactor (commit aa33e1a1)
    // each IbmMqProducer / IbmMqConsumer opens its own dedicated MQQueueManager via
    // IbmMqEndpoint.CreateDedicatedQueueManagerAsync to avoid MQI-call serialisation
    // deadlocks. The shared component pool (IbmMqComponent._connections) is still used
    // for endpoint-scoped MQI work (OpenQueueAsync / OpenTopicAsync / OpenTopicForPublishAsync)
    // and is exercised here directly via IbmMqEndpoint.GetQueueManagerAsync, NOT via
    // producer.Start (which goes to the dedicated path).

    [Fact]
    public async Task Pool_TwoEndpointsIdenticalParams_ShareSingleQueueManager()
    {
        var component = new IbmMqComponent();

        // Two endpoints on different queues but same connection identity \u2192 1 MQQueueManager
        var epA = CreateEndpoint(component, Queue);
        var epB = CreateEndpoint(component, "DEV.QUEUE.2");

        var qmA = await epA.GetQueueManagerAsync();
        var qmB = await epB.GetQueueManagerAsync();

        component.PooledConnectionCount.Should().Be(1, "both endpoints have identical inline params");
        var conns = await component.GetPooledConnectionsAsync();
        conns.Should().HaveCount(1);
        conns[0].IsConnected.Should().BeTrue();
        ReferenceEquals(qmA, qmB).Should().BeTrue("identical inline params share one pooled MQQueueManager");

        await epA.Stop(); await epB.Stop();
        await component.DisposeAsync();
        _output.WriteLine("IBM MQ shared QM: 2 endpoints, 1 MQQueueManager.");
    }

    [Fact]
    public async Task Pool_DifferentFactoryNames_ProduceDistinctQueueManagers()
    {
        var component = new IbmMqComponent();
        var ctx = new RouteContext();
        ctx.AddComponent(component);

        var f1 = new IbmMqConnectionFactory
        {
            Host = Host, Port = Port, Channel = Channel, QueueManager = QueueManager,
            User = User, Password = Password
        };
        var f2 = new IbmMqConnectionFactory
        {
            Host = Host, Port = Port, Channel = Channel, QueueManager = QueueManager,
            User = User, Password = Password
        };
        ctx.AddToRegistry("mq-1", f1);
        ctx.AddToRegistry("mq-2", f2);

        var epA = CreateEndpoint(component, Queue, "connectionFactory=mq-1");
        var epB = CreateEndpoint(component, Queue, "connectionFactory=mq-2");

        var qmA = await epA.GetQueueManagerAsync();
        var qmB = await epB.GetQueueManagerAsync();

        component.PooledConnectionCount.Should().Be(2,
            "two named factories produce two distinct MQQueueManager instances even when params match");
        var conns = await component.GetPooledConnectionsAsync();
        conns.Should().HaveCount(2);
        conns.Should().OnlyContain(c => c.IsConnected);
        ReferenceEquals(qmA, qmB).Should().BeFalse();

        await epA.Stop(); await epB.Stop();
        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task Pool_StopOneEndpoint_PooledQueueManagerRemains_OtherEndpointStillWorks()
    {
        var component = new IbmMqComponent();

        var epA = CreateEndpoint(component, Queue);
        var epB = CreateEndpoint(component, "DEV.QUEUE.2");

        await epA.GetQueueManagerAsync();
        await epB.GetQueueManagerAsync();

        component.PooledConnectionCount.Should().Be(1);

        await epA.Stop();

        var conns = await component.GetPooledConnectionsAsync();
        conns.Should().HaveCount(1);
        conns[0].IsConnected.Should().BeTrue("pooled MQQueueManager survives endpoint Stop");

        // epB still resolves to the same shared connection after epA stopped.
        var qmB2 = await epB.GetQueueManagerAsync();
        ReferenceEquals(qmB2, conns[0]).Should().BeTrue();

        await epB.Stop();
        await component.DisposeAsync();
    }

    [Fact]
    public async Task Pool_DisposeAsync_DisconnectsAllPooledQueueManagers()
    {
        var component = new IbmMqComponent();
        var endpoint = CreateEndpoint(component, Queue);

        await endpoint.GetQueueManagerAsync();

        var conns = await component.GetPooledConnectionsAsync();
        conns.Should().HaveCount(1);
        var pooled = conns[0];
        pooled.IsConnected.Should().BeTrue();

        await endpoint.Stop();
        await component.DisposeAsync();

        pooled.IsConnected.Should().BeFalse("DisposeAsync must disconnect all pooled MQQueueManager instances");
        component.PooledConnectionCount.Should().Be(0);
    }

    [Fact]
    public async Task Pool_RestartEndpoint_ReusesPooledQueueManager()
    {
        var component = new IbmMqComponent();

        var epA = CreateEndpoint(component, Queue);
        var initial = await epA.GetQueueManagerAsync();

        await epA.Stop();

        // Pool survives endpoint Stop \u2014 it is released only on Component.DisposeAsync.
        var afterStop = await component.GetPooledConnectionsAsync();
        afterStop.Should().HaveCount(1);
        afterStop[0].IsConnected.Should().BeTrue();
        ReferenceEquals(afterStop[0], initial).Should().BeTrue();

        // A fresh endpoint with identical params reuses the same pooled MQQueueManager.
        var epB = CreateEndpoint(component, Queue);
        var reused = await epB.GetQueueManagerAsync();

        component.PooledConnectionCount.Should().Be(1);
        ReferenceEquals(reused, initial).Should().BeTrue("pool reuses the existing MQQueueManager");

        await epB.Stop();
        await component.DisposeAsync();
    }
}
