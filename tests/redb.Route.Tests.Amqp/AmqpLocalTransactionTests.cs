using System.Net.Http.Headers;
using System.Text;
using Amqp;
using Amqp.Framing;
using Amqp.Types;
using redb.Route.Abstractions;
using redb.Route.Amqp;
using redb.Route.Core;
using redb.Route.Processors;
using redb.Route.Transactions;
using AmqpMessage = global::Amqp.Message;
using RouteMessage = redb.Route.Core.Message;

namespace redb.Route.Tests.Amqp;

/// <summary>
/// <c>localTransactions=true</c> against ActiveMQ Artemis: the sends a producer defers in a <c>.Transacted()</c> block commit
/// in one AMQP local transaction — all of them or none. The "none" case needs a send the broker refuses: an address under
/// <c>redb-full.#</c> holds at most 4 KB and fails what does not fit (set through Artemis' management, so the test does
/// not depend on the broker's configuration surviving a container restart).
/// </summary>
[Trait("Category", "Integration")]
public sealed class AmqpLocalTransactionTests : IAsyncLifetime
{
    private const string Host = "localhost";
    private const int Port = 5673;
    private const string Url = "amqp://admin:admin@localhost:5673";

    public async Task InitializeAsync()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes("admin:admin")));
        http.DefaultRequestHeaders.Add("Origin", "http://localhost");
        const string request = """
            {"type":"exec","mbean":"org.apache.activemq.artemis:broker=\"0.0.0.0\"",
             "operation":"addAddressSettings(java.lang.String,java.lang.String)",
             "arguments":["redb-full.#","{\"maxSizeBytes\":4096,\"pageSizeBytes\":1024,\"addressFullMessagePolicy\":\"FAIL\"}"]}
            """;
        var response = await http.PostAsync("http://localhost:8161/console/jolokia/",
            new StringContent(request, Encoding.UTF8, "application/json"));
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"status\":200", "the test sets up the full address itself");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<AmqpProducer> StartProducer(string address, string? parameters = "localTransactions=true")
    {
        var query = $"host={Host}&port={Port}&user=admin&password=admin";
        if (parameters is not null) query += $"&{parameters}";
        var endpoint = (AmqpEndpoint)new AmqpComponent().CreateEndpoint(EndpointUriParser.Parse($"amqp://{address}?{query}"));
        var producer = (AmqpProducer)endpoint.CreateProducer();
        await producer.Start();
        return producer;
    }

    private static Task InTransaction(IExchange exchange, Func<IExchange, CancellationToken, Task> body) =>
        new TransactedProcessor(new DelegateProcessor(body), new TransactionPolicy()).Process(exchange);

    private static async Task<List<string>> Drain(string address)
    {
        var connection = await Connection.Factory.CreateAsync(new Address(Url));
        try
        {
            var receiver = new ReceiverLink(new Session(connection), $"drain-{Guid.NewGuid():N}", new Attach
            {
                Source = new Source { Address = address, Capabilities = [new Symbol("queue")] },
                Target = new Target(),
            }, null);
            var bodies = new List<string>();
            while (await receiver.ReceiveAsync(TimeSpan.FromSeconds(2)) is { } message)
            {
                bodies.Add(message.Body switch { byte[] b => Encoding.UTF8.GetString(b), var body => body?.ToString() ?? "" });
                receiver.Accept(message);
            }
            return bodies;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    [Fact]
    public async Task The_sends_of_a_block_commit_in_one_transaction()
    {
        var address = $"redb-tx.{Guid.NewGuid():N}";
        var producer = await StartProducer(address);

        await InTransaction(new Exchange(new RouteMessage("one")), async (ex, ct) =>
        {
            await producer.Process(ex, ct);
            ex.In.Body = "two";
            await producer.Process(ex, ct);
        });
        await producer.Stop();

        (await Drain(address)).Should().Equal("one", "two");
    }

    [Fact]
    public async Task A_send_that_cannot_complete_takes_the_whole_transaction_down()
    {
        var address = $"redb-full.{Guid.NewGuid():N}";
        // timeout=3: Artemis does not refuse a send to a full address, it withholds the credit; the step times out.
        var producer = await StartProducer(address, "localTransactions=true&timeout=3");

        // Artemis takes a message while the address is under its limit, whatever the message's size: the first send
        // fills the 4 KB address, the second finds it full and gets no credit.
        var block = () => InTransaction(new Exchange(new RouteMessage(new string('x', 8_000))), async (ex, ct) =>
        {
            await producer.Process(ex, ct);
            ex.In.Body = "second";
            await producer.Process(ex, ct);
        });
        await block.Should().ThrowAsync<TimeoutException>();
        await producer.Stop();

        (await Drain(address)).Should().BeEmpty("the first send is discharged as failed with the second");
    }

    [Fact]
    public void It_contradicts_a_request_reply_producer()
    {
        var act = () => new AmqpComponent().CreateEndpoint(EndpointUriParser.Parse(
            $"amqp://q?host={Host}&port={Port}&localTransactions=true&replyTo=true"));

        act.Should().Throw<ArgumentException>().WithMessage("*localTransactions*replyTo*");
    }

    [Fact]
    public void It_contradicts_transacted_false()
    {
        var act = () => new AmqpComponent().CreateEndpoint(EndpointUriParser.Parse(
            $"amqp://q?host={Host}&port={Port}&localTransactions=true&transacted=false"));

        act.Should().Throw<ArgumentException>().WithMessage("*localTransactions*transacted=false*");
    }

    [Fact]
    public void The_builder_writes_it_out()
    {
        redb.Route.Amqp.Amqp.Address("q").LocalTransactions().Build().Should().Contain("localTransactions=true");
        redb.Route.Amqp.Amqp.Address("q").Build().Should().NotContain("localTransactions");
    }
}
