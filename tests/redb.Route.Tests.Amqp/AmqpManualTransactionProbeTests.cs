using System.Diagnostics;
using Amqp;
using Amqp.Framing;
using Amqp.Transactions;
using Amqp.Types;
using Xunit.Abstractions;

namespace redb.Route.Tests.Amqp;

/// <summary>
/// PROBE: does the broker (ActiveMQ Artemis on localhost:5673) answer an AMQP 1.0 transaction driven by hand — a
/// controller link to its coordinator, Declare, sends carrying TransactionalState, Discharge — the way the client's
/// System.Transactions path never got an answer to its discharge (docs/TRANSACTED_BATCH_COMMIT_2026_09_19.md)?
/// </summary>
[Trait("Category", "Integration")]
public sealed class AmqpManualTransactionProbeTests
{
    private const string Url = "amqp://admin:admin@localhost:5673";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly ITestOutputHelper _output;

    public AmqpManualTransactionProbeTests(ITestOutputHelper output) => _output = output;

    private static Task<Outcome> SendAsync(SenderLink link, Message message, DeliveryState? state)
    {
        var done = new TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        link.Send(message, state, (_, _, outcome, _) => done.TrySetResult(outcome), null);
        return done.Task.WaitAsync(Wait);
    }

    private static Message Control(object body) => new() { BodySection = new AmqpValue { Value = body } };

    private async Task<List<string>> Drain(Session session, string queue)
    {
        var receiver = new ReceiverLink(session, $"probe-receiver-{Guid.NewGuid():N}", new Attach
        {
            Source = new Source { Address = queue, Capabilities = [new Symbol("queue")] },
            Target = new Target(),
        }, null);
        var bodies = new List<string>();
        while (await receiver.ReceiveAsync(TimeSpan.FromSeconds(2)) is { } message)
        {
            bodies.Add(message.Body?.ToString() ?? "");
            receiver.Accept(message);
        }
        await receiver.CloseAsync();
        return bodies;
    }

    private async Task<List<string>> SendInATransaction(bool commit)
    {
        var queue = $"probe-tx-{Guid.NewGuid():N}";
        var connection = await Connection.Factory.CreateAsync(new Address(Url));
        try
        {
            var session = new Session(connection);
            var controller = new SenderLink(session, $"probe-controller-{Guid.NewGuid():N}", new Attach
            {
                Source = new Source(),
                Target = new Coordinator { Capabilities = [TxnCapabilities.LocalTransactions] },
            }, null);
            // "queue": an anycast address on Artemis, which keeps what nobody has subscribed to yet.
            var sender = new SenderLink(session, $"probe-sender-{Guid.NewGuid():N}", new Attach
            {
                Source = new Source(),
                Target = new Target { Address = queue, Capabilities = [new Symbol("queue")] },
            }, null);

            var clock = Stopwatch.StartNew();
            var declared = await SendAsync(controller, Control(new Declare()), null);
            _output.WriteLine($"declare -> {declared} in {clock.ElapsedMilliseconds} ms");
            var txnId = declared.Should().BeOfType<Declared>().Subject.TxnId;

            foreach (var body in new[] { "one", "two" })
            {
                var sent = await SendAsync(sender, new Message(body), new TransactionalState { TxnId = txnId });
                _output.WriteLine($"send {body} -> {sent}");
            }

            clock.Restart();
            var discharged = await SendAsync(controller, Control(new Discharge { TxnId = txnId, Fail = !commit }), null);
            _output.WriteLine($"discharge(fail={!commit}) -> {discharged} in {clock.ElapsedMilliseconds} ms");
            discharged.Should().BeOfType<Accepted>();

            await sender.CloseAsync();
            await controller.CloseAsync();
            return await Drain(session, queue);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    [Fact]
    public async Task A_committed_transaction_delivers_its_messages()
    {
        (await SendInATransaction(commit: true)).Should().Equal("one", "two");
    }

    [Fact]
    public async Task A_failed_transaction_delivers_nothing()
    {
        (await SendInATransaction(commit: false)).Should().BeEmpty();
    }
}
