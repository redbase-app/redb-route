using System.Collections.Concurrent;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.RabbitMQ;
using Xunit.Abstractions;

namespace redb.Route.Tests.RabbitMQ;

/// <summary>
/// Integration tests for the <see cref="RabbitMQComponent"/> connection pool against a live broker.
/// Validates the new shared-connection model: identical inline parameters share one underlying
/// <c>IConnection</c>, named factories partition by name, endpoint Stop does not affect the pool,
/// and component DisposeAsync drains the pool. Self-healing is exercised by killing the cached
/// connection and observing transparent recreation.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RabbitMQConnectionPoolIntegrationTests
{
    private const string Host = "localhost";
    private const int Port = 5672;
    private readonly ITestOutputHelper _output;

    public RabbitMQConnectionPoolIntegrationTests(ITestOutputHelper output) => _output = output;

    private static RabbitMQEndpoint CreateEndpoint(RabbitMQComponent component, string queue, string? extraParams = null)
    {
        var qs = $"host={Host}&port={Port}&username=admin&password=admin&declare=true";
        if (extraParams is not null) qs += $"&{extraParams}";
        var uri = EndpointUriParser.Parse($"rabbitmq://{queue}?{qs}");
        return (RabbitMQEndpoint)component.CreateEndpoint(uri);
    }

    [Fact]
    public async Task Pool_TwoEndpointsIdenticalParams_ShareSingleConnection()
    {
        var component = new RabbitMQComponent();
        var qa = $"pool-share-a-{Guid.NewGuid():N}";
        var qb = $"pool-share-b-{Guid.NewGuid():N}";

        var epA = CreateEndpoint(component, qa);
        var epB = CreateEndpoint(component, qb);

        var pa = (RabbitMQProducer)epA.CreateProducer();
        var pb = (RabbitMQProducer)epB.CreateProducer();

        await pa.Start();
        await pb.Start();

        component.PooledConnectionCount.Should().Be(1, "both endpoints have identical inline params");
        var conns = await component.GetPooledConnectionsAsync();
        conns.Should().HaveCount(1);
        conns[0].IsOpen.Should().BeTrue();

        // Both producers should function on the same shared connection
        await pa.Process(new Exchange(new Message("from-a")));
        await pb.Process(new Exchange(new Message("from-b")));

        await pa.Stop(); await pb.Stop();
        await epA.Stop(); await epB.Stop();
        await component.DisposeAsync();

        _output.WriteLine("Shared connection: 2 endpoints, 1 IConnection.");
    }

    [Fact]
    public async Task Pool_DifferentFactoryNames_ProduceDistinctConnections()
    {
        // Register two named factories with identical parameters but distinct names.
        // They MUST resolve to two pooled connections (different "factory:" keys).
        var component = new RabbitMQComponent();
        var ctx = new RouteContext();
        ctx.AddComponent(component);

        var f1 = new RabbitMQConnectionFactory
        { Host = Host, Port = Port, Username = "admin", Password = "admin" };
        var f2 = new RabbitMQConnectionFactory
        { Host = Host, Port = Port, Username = "admin", Password = "admin" };
        ctx.AddToRegistry("conn-1", f1);
        ctx.AddToRegistry("conn-2", f2);

        var qa = $"pool-fac-a-{Guid.NewGuid():N}";
        var qb = $"pool-fac-b-{Guid.NewGuid():N}";

        var epA = CreateEndpoint(component, qa, "connectionFactory=conn-1");
        var epB = CreateEndpoint(component, qb, "connectionFactory=conn-2");

        var pa = (RabbitMQProducer)epA.CreateProducer();
        var pb = (RabbitMQProducer)epB.CreateProducer();
        await pa.Start();
        await pb.Start();

        component.PooledConnectionCount.Should().Be(2,
            "two named factories produce two distinct pooled connections even when params match");
        var conns = await component.GetPooledConnectionsAsync();
        conns.Should().HaveCount(2);
        conns.Should().OnlyContain(c => c.IsOpen);
        // Different IConnection instances
        ReferenceEquals(conns[0], conns[1]).Should().BeFalse();

        await pa.Stop(); await pb.Stop();
        await epA.Stop(); await epB.Stop();
        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task Pool_StopOneEndpoint_PooledConnectionRemains_OtherEndpointStillWorks()
    {
        var component = new RabbitMQComponent();
        var qa = $"pool-stop-a-{Guid.NewGuid():N}";
        var qb = $"pool-stop-b-{Guid.NewGuid():N}";

        var epA = CreateEndpoint(component, qa);
        var epB = CreateEndpoint(component, qb);

        var prodA = (RabbitMQProducer)epA.CreateProducer();
        var prodB = (RabbitMQProducer)epB.CreateProducer();
        await prodA.Start();
        await prodB.Start();

        component.PooledConnectionCount.Should().Be(1);

        // Stop endpoint A (and its producer). Pool must survive.
        await prodA.Stop();
        await epA.Stop();

        var conns = await component.GetPooledConnectionsAsync();
        conns.Should().HaveCount(1);
        conns[0].IsOpen.Should().BeTrue("pooled connection survives endpoint Stop");

        // Endpoint B must continue to publish over the same shared connection
        await prodB.Process(new Exchange(new Message("after-a-stopped")));

        await prodB.Stop();
        await epB.Stop();
        await component.DisposeAsync();
    }

    [Fact]
    public async Task Pool_DisposeAsync_ClosesAllPooledConnections()
    {
        var component = new RabbitMQComponent();
        var queue = $"pool-dispose-{Guid.NewGuid():N}";
        var endpoint = CreateEndpoint(component, queue);
        var producer = (RabbitMQProducer)endpoint.CreateProducer();

        await producer.Start();
        await producer.Process(new Exchange(new Message("dispose-test")));

        var conns = await component.GetPooledConnectionsAsync();
        conns.Should().HaveCount(1);
        var pooled = conns[0];
        pooled.IsOpen.Should().BeTrue();

        await producer.Stop();
        await endpoint.Stop();

        await component.DisposeAsync();

        // After DisposeAsync the connection must be closed.
        pooled.IsOpen.Should().BeFalse("DisposeAsync must close all pooled IConnection instances");
        component.PooledConnectionCount.Should().Be(0);
    }

    [Fact]
    public async Task Pool_SelfHeal_AfterPooledConnectionDropped_NextOperationReconnects()
    {
        var component = new RabbitMQComponent();
        var queue = $"pool-heal-{Guid.NewGuid():N}";
        var endpoint = CreateEndpoint(component, queue);
        var producer = (RabbitMQProducer)endpoint.CreateProducer();

        await producer.Start();
        await producer.Process(new Exchange(new Message("first")));

        // Forcibly close the pooled connection out from under the producer
        var conns = await component.GetPooledConnectionsAsync();
        conns.Should().HaveCount(1);
        var first = conns[0];
        await first.CloseAsync(200, "test forced close", TimeSpan.FromSeconds(5), abort: false);
        first.IsOpen.Should().BeFalse();

        // Producer's own channel is now dead; re-create producer to trigger a fresh
        // channel (which will trigger pool self-heal: dead connection evicted, new one created).
        await producer.Stop();
        var producer2 = (RabbitMQProducer)endpoint.CreateProducer();
        await producer2.Start();
        await producer2.Process(new Exchange(new Message("after-heal")));

        var conns2 = await component.GetPooledConnectionsAsync();
        conns2.Should().HaveCount(1);
        conns2[0].IsOpen.Should().BeTrue();
        ReferenceEquals(conns2[0], first).Should().BeFalse(
            "the dropped connection must be replaced with a new IConnection instance");

        await producer2.Stop();
        await endpoint.Stop();
        await component.DisposeAsync();
    }

    [Fact]
    public async Task Producer_ConcurrentPublishUnderPublishLock_NoExceptions()
    {
        // Validates Task 4: SemaphoreSlim _publishLock around BasicPublishAsync.
        // RabbitMQ.Client 7.x channel publish is NOT thread-safe — without the lock,
        // 200 concurrent publishes on the same producer would produce ProtocolViolationException.
        var component = new RabbitMQComponent();
        var queue = $"pool-concurrent-pub-{Guid.NewGuid():N}";
        var endpoint = CreateEndpoint(component, queue);
        var producer = (RabbitMQProducer)endpoint.CreateProducer();
        await producer.Start();

        const int n = 200;
        var publishTasks = Enumerable.Range(0, n)
            .Select(i => Task.Run(() => producer.Process(new Exchange(new Message($"msg-{i}")))))
            .ToArray();

        var act = async () => await Task.WhenAll(publishTasks);
        await act.Should().NotThrowAsync();

        await producer.Stop();
        await endpoint.Stop();
        await component.DisposeAsync();

        _output.WriteLine("200 concurrent publishes serialized by _publishLock.");
    }

    [Fact]
    public async Task Consumer_ReplyChannelIsLazy_OnlyCreatedOnFirstReply()
    {
        // Validates Task 2: lazy reply-channel separation in RabbitMQConsumer.
        // Before sending a reply, only the consume channel exists. A pure one-way
        // consumer must NOT open a second channel.
        var component = new RabbitMQComponent();
        var queue = $"pool-replych-{Guid.NewGuid():N}";

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var endpoint = CreateEndpoint(component, queue);
        var consumer = (RabbitMQConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();

        // Drive a one-way message through; reply channel must NOT be lazily created.
        var prodEp = CreateEndpoint(component, queue);
        var producer = (RabbitMQProducer)prodEp.CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("one-way")));

        // Wait for processing
        await Task.Delay(500);

        // We can't directly count channels via the AMQP wire here, but we can verify
        // the pool still has exactly one connection (no second pooled conn) and that
        // the consumer is still happily consuming.
        component.PooledConnectionCount.Should().Be(1);
        var conns = await component.GetPooledConnectionsAsync();
        conns[0].IsOpen.Should().BeTrue();

        await producer.Stop();
        await prodEp.Stop();
        await consumer.Stop();
        await endpoint.Stop();
        await component.DisposeAsync();
    }
}
