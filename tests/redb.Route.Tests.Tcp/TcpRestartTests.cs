using System.Net;
using System.Net.Sockets;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Tcp;

namespace redb.Route.Tests.Tcp;

/// <summary>
/// A TCP consumer and producer keep working after <c>Stop()</c> and <c>Start()</c> of the same route context: the stop
/// closes the listening socket, the start binds the port again on the cached endpoint, and the producer still connects.
/// </summary>
public sealed class TcpRestartTests
{
    private static async Task<string?> AskAsync(int port, string text)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text + "\n"));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Consumer_and_producer_work_after_the_context_is_stopped_and_started_again()
    {
        var port = global::redb.Route.Tests.Shared.TestPorts.Next();
        await using var ctx = new RouteContext();
        ctx.AddComponent(new TcpComponent());
        ctx.AddRoutes(r => r.From($"tcp://127.0.0.1:{port}?framing=TextLine&inOut=true")
            .Process(e => e.Out = new Message("re:" + e.In.Body)));
        ctx.AddRoutes(r => r.From("direct://call").To($"tcp://127.0.0.1:{port}?framing=TextLine&inOut=true"));

        async Task CheckAsync(string body)
        {
            (await AskAsync(port, body)).Should().Be("re:" + body, "TCP-приёмник после рестарта отвечает");

            var producer = ctx.GetEndpoint("direct://call").CreateProducer();
            await producer.Start();
            var exchange = new Exchange(new Message(body + "-p"));
            await producer.Process(exchange);
            exchange.Exception.Should().BeNull("TCP-отправка после рестарта доходит");
            (exchange.HasOut ? exchange.Out! : exchange.In).Body?.ToString().Should().Be("re:" + body + "-p");
        }

        await ctx.Start();
        await CheckAsync("first");

        await ctx.Stop();
        await ctx.Start();
        await CheckAsync("second");
    }
}
