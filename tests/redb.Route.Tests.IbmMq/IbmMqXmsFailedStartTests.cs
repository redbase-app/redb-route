using System.Net.NetworkInformation;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.IbmMq;

namespace redb.Route.Tests.IbmMq;

/// <summary>
/// An XMS listener consumer whose start fails part-way (the connection is up, the queue does not exist, so the consumer
/// is refused) closes the connection it opened: repeated failed starts leave no half-open connection to the broker.
/// Counted from this process's established TCP connections to the listener port, against a real IBM MQ broker
/// (localhost:1414, QM1, DEV.APP.SVRCONN, app/admin).
/// </summary>
[Trait("Category", "Integration")]
[Collection("IbmMqConnectionCount")]
public sealed class IbmMqXmsFailedStartTests
{
    private const int Port = 1414;

    private static IbmMqEndpoint Endpoint(string destination)
        => (IbmMqEndpoint)new IbmMqComponent().CreateEndpoint(EndpointUriParser.Parse(
            $"wmq:{destination}?host=localhost&port={Port}&channel=DEV.APP.SVRCONN&queueManager=QM1" +
            "&user=app&password=admin&receiveMode=listener"));

    private static int BrokerConnections() =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
            .Count(c => c.RemoteEndPoint.Port == Port && c.State == TcpState.Established);

    private static async Task<int> SettledBrokerConnections()
    {
        // A closed socket leaves ESTABLISHED within moments; read until two samples agree.
        var last = BrokerConnections();
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(250);
            var now = BrokerConnections();
            if (now == last)
                return now;
            last = now;
        }
        return last;
    }

    [Fact]
    public async Task Failed_starts_on_a_missing_queue_leave_no_connection_open()
    {
        var endpoint = Endpoint($"REDB.MISSING.{Guid.NewGuid():N}");
        var consumer = (IbmMqConsumer)endpoint.CreateConsumer(Substitute.For<IProcessor>());
        var before = await SettledBrokerConnections();

        for (var i = 0; i < 3; i++)
        {
            var start = () => consumer.Start();
            await start.Should().ThrowAsync<Exception>("the queue does not exist, so the consumer is refused");
        }

        var after = await SettledBrokerConnections();
        await endpoint.Stop();

        after.Should().Be(before, "each failed start closes the connection it opened");
    }
}

[CollectionDefinition("IbmMqConnectionCount", DisableParallelization = true)]
public sealed class IbmMqConnectionCountCollection;
