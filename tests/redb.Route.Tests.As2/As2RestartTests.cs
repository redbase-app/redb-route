using System.Text;
using AwesomeAssertions;
using redb.Route.Abstractions;
using redb.Route.As2;
using redb.Route.Core;
using As2Dsl = redb.Route.As2.Fluent.As2;

namespace redb.Route.Tests.As2;

/// <summary>
/// An AS2 consumer and producer keep working after <c>Stop()</c> and <c>Start()</c> of the same route context: the stop
/// gives the listener path back, the start registers it again on the cached endpoint, and a message is signed,
/// encrypted, received and acknowledged with an MDN on both runs.
/// </summary>
public class As2RestartTests
{
    [Fact]
    public async Task Consumer_and_producer_work_after_the_context_is_stopped_and_started_again()
    {
        var port = As2TestKit.FreePort();
        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("me", As2TestKit.Receiver());
        context.AddToRegistry("them", As2TestKit.Sender());
        var received = new List<string>();
        context.AddRoutes(r =>
            r.From(As2Dsl.Receive("/inbound").Host("127.0.0.1").Port(port).ConnectionFactory("me"))
                .Process(e => { lock (received) received.Add(Encoding.UTF8.GetString((byte[])e.In.Body!)); }));
        context.AddRoutes(r =>
            r.From("direct://send").To(As2Dsl.Send($"http://127.0.0.1:{port}/inbound").ConnectionFactory("them")));

        async Task SendAsync(string payload)
        {
            var producer = context.GetEndpoint("direct://send").CreateProducer();
            await producer.Start();
            var exchange = new Exchange(new Message(payload) { ContentType = "application/edi-x12" });
            await producer.Process(exchange);
            exchange.Exception.Should().BeNull("AS2-отправка после рестарта доходит и получает MDN");
        }

        await context.Start();
        await SendAsync("ISA*first~");

        await context.Stop();
        await context.Start();
        await SendAsync("ISA*second~");

        lock (received) received.Should().Equal(["ISA*first~", "ISA*second~"], "AS2-приёмник после рестарта принимает");
    }
}
