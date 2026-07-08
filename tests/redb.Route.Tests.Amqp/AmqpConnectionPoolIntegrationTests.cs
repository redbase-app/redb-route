using redb.Route.Abstractions;
using redb.Route.Amqp;
using redb.Route.Core;
using Xunit.Abstractions;

namespace redb.Route.Tests.Amqp;

/// <summary>
/// Integration tests for the <see cref="AmqpComponent"/> connection pool against a live AMQP 1.0
/// broker (ActiveMQ Artemis). Validates the new shared-connection model: identical inline
/// parameters share one underlying <c>Connection</c>, named factories partition by name,
/// endpoint Stop does not affect the pool, and component DisposeAsync drains the pool.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AmqpConnectionPoolIntegrationTests
{
    private const string Host = "localhost";
    private const int Port = 5673;
    private const string User = "admin";
    private const string Password = "admin";
    private readonly ITestOutputHelper _output;

    public AmqpConnectionPoolIntegrationTests(ITestOutputHelper output) => _output = output;

    private static AmqpEndpoint CreateEndpoint(AmqpComponent component, string address, string? extraParams = null)
    {
        var qs = $"host={Host}&port={Port}&user={User}&password={Password}";
        if (extraParams is not null) qs += $"&{extraParams}";
        var uri = EndpointUriParser.Parse($"amqp://{address}?{qs}");
        return (AmqpEndpoint)component.CreateEndpoint(uri);
    }

    [Fact]
    public async Task Pool_TwoEndpointsIdenticalParams_ShareSingleConnection()
    {
        var component = new AmqpComponent();
        var addrA = $"pool.share.a.{Guid.NewGuid():N}";
        var addrB = $"pool.share.b.{Guid.NewGuid():N}";

        var epA = CreateEndpoint(component, addrA);
        var epB = CreateEndpoint(component, addrB);

        var pa = (AmqpProducer)epA.CreateProducer();
        var pb = (AmqpProducer)epB.CreateProducer();
        await pa.Start();
        await pb.Start();

        component.PooledConnectionCount.Should().Be(1, "both endpoints have identical inline params");
        var conns = await component.GetPooledConnectionsAsync();
        conns.Should().HaveCount(1);
        conns[0].IsClosed.Should().BeFalse();

        await pa.Process(new Exchange(new Message("from-a")));
        await pb.Process(new Exchange(new Message("from-b")));

        await pa.Stop(); await pb.Stop();
        await epA.Stop(); await epB.Stop();
        await component.DisposeAsync();

        _output.WriteLine("AMQP shared connection: 2 endpoints, 1 Connection.");
    }

    [Fact]
    public async Task Pool_DifferentFactoryNames_ProduceDistinctConnections()
    {
        var component = new AmqpComponent();
        var ctx = new RouteContext();
        ctx.AddComponent(component);

        var f1 = new AmqpConnectionFactory
        { Host = Host, Port = Port, User = User, Password = Password };
        var f2 = new AmqpConnectionFactory
        { Host = Host, Port = Port, User = User, Password = Password };
        ctx.AddToRegistry("amqp-1", f1);
        ctx.AddToRegistry("amqp-2", f2);

        var addrA = $"pool.fac.a.{Guid.NewGuid():N}";
        var addrB = $"pool.fac.b.{Guid.NewGuid():N}";

        var epA = CreateEndpoint(component, addrA, "connectionFactory=amqp-1");
        var epB = CreateEndpoint(component, addrB, "connectionFactory=amqp-2");

        var pa = (AmqpProducer)epA.CreateProducer();
        var pb = (AmqpProducer)epB.CreateProducer();
        await pa.Start();
        await pb.Start();

        component.PooledConnectionCount.Should().Be(2,
            "two named factories produce two distinct pooled connections even when params match");
        var conns = await component.GetPooledConnectionsAsync();
        conns.Should().HaveCount(2);
        conns.Should().OnlyContain(c => !c.IsClosed);
        ReferenceEquals(conns[0], conns[1]).Should().BeFalse();

        await pa.Stop(); await pb.Stop();
        await epA.Stop(); await epB.Stop();
        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task Pool_StopOneEndpoint_PooledConnectionRemains_OtherEndpointStillWorks()
    {
        var component = new AmqpComponent();
        var addrA = $"pool.stop.a.{Guid.NewGuid():N}";
        var addrB = $"pool.stop.b.{Guid.NewGuid():N}";

        var epA = CreateEndpoint(component, addrA);
        var epB = CreateEndpoint(component, addrB);

        var prodA = (AmqpProducer)epA.CreateProducer();
        var prodB = (AmqpProducer)epB.CreateProducer();
        await prodA.Start();
        await prodB.Start();

        component.PooledConnectionCount.Should().Be(1);

        await prodA.Stop();
        await epA.Stop();

        var conns = await component.GetPooledConnectionsAsync();
        conns.Should().HaveCount(1);
        conns[0].IsClosed.Should().BeFalse("pooled connection survives endpoint Stop");

        await prodB.Process(new Exchange(new Message("after-a-stopped")));

        await prodB.Stop();
        await epB.Stop();
        await component.DisposeAsync();
    }

    [Fact]
    public async Task Pool_DisposeAsync_ClosesAllPooledConnections()
    {
        var component = new AmqpComponent();
        var address = $"pool.dispose.{Guid.NewGuid():N}";
        var endpoint = CreateEndpoint(component, address);
        var producer = (AmqpProducer)endpoint.CreateProducer();

        await producer.Start();
        await producer.Process(new Exchange(new Message("dispose-test")));

        var conns = await component.GetPooledConnectionsAsync();
        conns.Should().HaveCount(1);
        var pooled = conns[0];
        pooled.IsClosed.Should().BeFalse();

        await producer.Stop();
        await endpoint.Stop();

        await component.DisposeAsync();

        pooled.IsClosed.Should().BeTrue("DisposeAsync must close all pooled Connection instances");
        component.PooledConnectionCount.Should().Be(0);
    }

    [Fact]
    public async Task Pool_RestartEndpoint_ReusesPooledConnection()
    {
        // Validates lifecycle: Stop+restart of the endpoint reuses the already-pooled connection.
        var component = new AmqpComponent();
        var address = $"pool.restart.{Guid.NewGuid():N}";

        var epA = CreateEndpoint(component, address);
        var prodA = (AmqpProducer)epA.CreateProducer();
        await prodA.Start();
        await prodA.Process(new Exchange(new Message("first")));

        var initial = (await component.GetPooledConnectionsAsync())[0];

        await prodA.Stop();
        await epA.Stop();

        // Pooled connection survives Stop
        var afterStop = await component.GetPooledConnectionsAsync();
        afterStop.Should().HaveCount(1);
        afterStop[0].IsClosed.Should().BeFalse();
        ReferenceEquals(afterStop[0], initial).Should().BeTrue();

        // Recreate endpoint with same params; new producer must use the existing pooled conn.
        var epB = CreateEndpoint(component, address);
        var prodB = (AmqpProducer)epB.CreateProducer();
        await prodB.Start();
        await prodB.Process(new Exchange(new Message("second")));

        component.PooledConnectionCount.Should().Be(1);
        var conns = await component.GetPooledConnectionsAsync();
        ReferenceEquals(conns[0], initial).Should().BeTrue("pool reuses the existing Connection");

        await prodB.Stop();
        await epB.Stop();
        await component.DisposeAsync();
    }
}
