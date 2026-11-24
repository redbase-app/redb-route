using System.Text;
using IBM.WMQ;
using redb.Route.Core;
using redb.Route.IbmMq;
using Xunit.Abstractions;

namespace redb.Route.Tests.IbmMq;

/// <summary>
/// The IBM MQ provider's chain for a poison message, in both receive modes (listener mode: the XMS client's own): the input queue's own BOTHRESH and BOQNAME
/// when the endpoint sets nothing, the endpoint's backoutThreshold and backoutQueue over them, and the queue manager's
/// dead letter queue (with a dead letter header) when a threshold is in force and there is no backout queue. Expects
/// IBM MQ at localhost:1414 with the queues of <c>tests/docker/ibmmq/redb-tests.mqsc</c>.
/// </summary>
[Trait("Category", "Integration")]
[Collection("IbmMqIntegration")]
public sealed class IbmMqPoisonCanonTests(ITestOutputHelper output)
{
    private const string Connection =
        "host=localhost&port=1414&channel=DEV.APP.SVRCONN&queueManager=QM1&user=app&password=admin";

    private static string DeadLetterQueue()
    {
        using var qm = IbmMqPoisonTests.Connect();
        var name = qm.DeadLetterQueueName?.Trim() ?? "";
        qm.Disconnect();
        return name;
    }

    /// <summary>Runs a route that always fails on <paramref name="queue"/> until <paramref name="landed"/> sees the message.</summary>
    private static async Task<int> RunPoison(string queue, string options, string marker, Func<bool> landed)
    {
        var runs = 0;
        await using var context = new RouteContext();
        context.AddComponent(new IbmMqComponent());
        context.AddRoutes(r => r
            .From($"wmq:{queue}?{Connection}&transacted=true&{options}")
            .Process(_ =>
            {
                Interlocked.Increment(ref runs);
                throw new InvalidOperationException("always fails");
            }));
        await context.Start();
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!landed() && DateTime.UtcNow < deadline)
            await Task.Delay(500);
        return runs;
    }

    [Theory]
    [InlineData("poll")]
    [InlineData("listener")]
    public async Task Without_endpoint_options_the_queues_own_BOTHRESH_and_BOQNAME_apply(string receiveMode)
    {
        IbmMqPoisonTests.Drain("DEV.QUEUE.ATTR");
        IbmMqPoisonTests.Drain("DEV.QUEUE.ATTR.BOQ");
        var marker = $"attr-{Guid.NewGuid():N}";
        IbmMqPoisonTests.Put("DEV.QUEUE.ATTR", marker);
        var moved = new List<string>();

        var runs = await RunPoison("DEV.QUEUE.ATTR", $"receiveMode={receiveMode}", marker, () =>
        {
            moved.AddRange(IbmMqPoisonTests.Drain("DEV.QUEUE.ATTR.BOQ"));
            return moved.Contains(marker);
        });

        output.WriteLine($"{receiveMode}: runs={runs}, moved={string.Join(",", moved)}");
        moved.Should().Contain(marker);
        runs.Should().Be(2, "BOTHRESH(2): the route ran at backout counts 0 and 1");
    }

    [Theory]
    [InlineData("poll")]
    [InlineData("listener")]
    public async Task The_endpoints_options_override_the_queues_attributes(string receiveMode)
    {
        IbmMqPoisonTests.Drain("DEV.QUEUE.ATTR");
        IbmMqPoisonTests.Drain("DEV.QUEUE.POISON.BOQ");
        var marker = $"override-{Guid.NewGuid():N}";
        IbmMqPoisonTests.Put("DEV.QUEUE.ATTR", marker);
        var moved = new List<string>();

        var runs = await RunPoison("DEV.QUEUE.ATTR",
            $"receiveMode={receiveMode}&backoutThreshold=1&backoutQueue=DEV.QUEUE.POISON.BOQ", marker, () =>
            {
                moved.AddRange(IbmMqPoisonTests.Drain("DEV.QUEUE.POISON.BOQ"));
                return moved.Contains(marker);
            });

        output.WriteLine($"{receiveMode}: runs={runs}, moved={string.Join(",", moved)}");
        moved.Should().Contain(marker);
        runs.Should().Be(1, "backoutThreshold=1 on the endpoint wins over the queue's BOTHRESH(2)");
    }

    [Theory]
    [InlineData("poll")]
    [InlineData("listener")]
    public async Task A_threshold_without_a_backout_queue_goes_to_the_dead_letter_queue_with_a_dead_letter_header(string receiveMode)
    {
        var deadLetterQueue = DeadLetterQueue();
        output.WriteLine($"queue manager DEADQ: '{deadLetterQueue}'");
        deadLetterQueue.Should().NotBeEmpty("the stand's queue manager has a dead letter queue");
        IbmMqPoisonTests.Drain("DEV.QUEUE.NOBOQ");
        var marker = $"dlq-{Guid.NewGuid():N}";
        IbmMqPoisonTests.Put("DEV.QUEUE.NOBOQ", marker);
        MQMessage? found = null;

        var runs = await RunPoison("DEV.QUEUE.NOBOQ", $"receiveMode={receiveMode}", marker, () =>
        {
            found ??= TakeFromDeadLetterQueue(deadLetterQueue, marker);
            return found is not null;
        });

        output.WriteLine($"{receiveMode}: runs={runs}, dlq format={found?.Format}");
        found.Should().NotBeNull("the message reached the queue manager's dead letter queue");
        found!.Format.Should().Be(MQC.MQFMT_DEAD_LETTER_HEADER);
        runs.Should().Be(2, "BOTHRESH(2) on the queue");
    }

    private static MQMessage? TakeFromDeadLetterQueue(string queue, string marker)
    {
        using var qm = IbmMqPoisonTests.Connect();
        var q = qm.AccessQueue(queue, MQC.MQOO_INPUT_SHARED | MQC.MQOO_FAIL_IF_QUIESCING);
        try
        {
            var gmo = new MQGetMessageOptions { Options = MQC.MQGMO_NO_WAIT | MQC.MQGMO_NO_SYNCPOINT };
            while (true)
            {
                var message = new MQMessage();
                try { q.Get(message, gmo); }
                catch (MQException ex) when (ex.ReasonCode == MQC.MQRC_NO_MSG_AVAILABLE) { return null; }
                var bytes = message.ReadBytes(message.MessageLength);
                // The XMS client puts the text of a message it dead-letters in UTF-16; the MQI path keeps the bytes.
                if (Encoding.UTF8.GetString(bytes).Contains(marker, StringComparison.Ordinal)
                    || Encoding.BigEndianUnicode.GetString(bytes).Contains(marker, StringComparison.Ordinal)
                    || Encoding.Unicode.GetString(bytes).Contains(marker, StringComparison.Ordinal)
                    || Encoding.BigEndianUnicode.GetString(bytes, 1, bytes.Length - 1).Contains(marker, StringComparison.Ordinal)
                    || Encoding.Unicode.GetString(bytes, 1, bytes.Length - 1).Contains(marker, StringComparison.Ordinal))
                    return message;
            }
        }
        finally
        {
            q.Close();
            qm.Disconnect();
        }
    }
}
