using System.Collections;
using IBM.WMQ;
using redb.Route.Core;
using redb.Route.IbmMq;

namespace redb.Route.Tests.IbmMq;

/// <summary>
/// The failed-message rule of the IBM MQ consumer, in both receive modes: a transacted consumer backs a failed message
/// out, and once the message was backed out <c>backoutThreshold</c> times it goes to the backout queue instead of the
/// route, as the IBM MQ classes for JMS requeue it. Expects IBM MQ at localhost:1414 with the queues of
/// <c>tests/docker/ibmmq/redb-tests.mqsc</c>.
/// </summary>
[Trait("Category", "Integration")]
[Collection("IbmMqIntegration")]
public sealed class IbmMqPoisonTests
{
    private const string Queue = "DEV.QUEUE.POISON";
    private const string BackoutQueue = "DEV.QUEUE.POISON.BOQ";
    private const string Connection =
        "host=localhost&port=1414&channel=DEV.APP.SVRCONN&queueManager=QM1&user=app&password=admin";

    internal static MQQueueManager Connect() => new("QM1", new Hashtable
    {
        [MQC.HOST_NAME_PROPERTY] = "localhost",
        [MQC.PORT_PROPERTY] = 1414,
        [MQC.CHANNEL_PROPERTY] = "DEV.APP.SVRCONN",
        [MQC.USER_ID_PROPERTY] = "app",
        [MQC.PASSWORD_PROPERTY] = "admin",
        [MQC.TRANSPORT_PROPERTY] = MQC.TRANSPORT_MQSERIES_MANAGED,
    });

    /// <summary>Empties a queue and returns the bodies it held.</summary>
    internal static List<string> Drain(string queue, int waitMs = 0)
    {
        var bodies = new List<string>();
        using var qm = Connect();
        var q = qm.AccessQueue(queue, MQC.MQOO_INPUT_SHARED | MQC.MQOO_FAIL_IF_QUIESCING);
        try
        {
            var gmo = new MQGetMessageOptions { Options = MQC.MQGMO_WAIT | MQC.MQGMO_NO_SYNCPOINT, WaitInterval = waitMs };
            while (true)
            {
                var message = new MQMessage();
                try { q.Get(message, gmo); }
                catch (MQException ex) when (ex.ReasonCode == MQC.MQRC_NO_MSG_AVAILABLE) { return bodies; }
                bodies.Add(message.ReadString(message.MessageLength));
                gmo.WaitInterval = 0;
            }
        }
        finally
        {
            q.Close();
            qm.Disconnect();
        }
    }

    internal static void Put(string queue, params string[] bodies)
    {
        using var qm = Connect();
        var q = qm.AccessQueue(queue, MQC.MQOO_OUTPUT | MQC.MQOO_FAIL_IF_QUIESCING);
        foreach (var body in bodies)
        {
            var message = new MQMessage { Format = MQC.MQFMT_STRING };
            message.WriteString(body);
            q.Put(message);
        }
        q.Close();
        qm.Disconnect();
    }

    [Theory]
    [InlineData("poll")]
    [InlineData("listener")]
    public async Task A_message_backed_out_backoutThreshold_times_goes_to_the_backout_queue_not_the_route(string receiveMode)
    {
        Drain(Queue);
        Drain(BackoutQueue);
        Put(Queue, "poison");
        var runs = 0;

        await using (var context = new RouteContext())
        {
            context.AddComponent(new IbmMqComponent());
            context.AddRoutes(r => r
                .From($"wmq:{Queue}?{Connection}&receiveMode={receiveMode}&transacted=true&backoutThreshold=2&backoutQueue={BackoutQueue}")
                .Process(_ =>
                {
                    Interlocked.Increment(ref runs);
                    throw new InvalidOperationException("always fails");
                }));
            await context.Start();

            Drain(BackoutQueue, waitMs: 30_000).Should().Equal("poison");
        }

        runs.Should().Be(2, "the route ran on backout counts 0 and 1; at 2 the message went to the backout queue");
        Drain(Queue).Should().BeEmpty();
    }
}
