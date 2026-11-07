using System.Net;
using System.Net.Sockets;
using System.Text;
using redb.Route.Core;
using redb.Route.Mail;

namespace redb.Route.Tests.Mail;

/// <summary>
/// The SMTP producer keeps its connection between sends. A server that closes an idle connection (GreenMail's socket
/// timeout answers the next command with "421 Service shutting down") must not lose the next message: the producer sends
/// it once more on a new connection. A refusal on a fresh connection is the server's answer and is not repeated.
/// </summary>
public sealed class SmtpKeptConnectionTests : IAsyncLifetime
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private Task? _server;

    private int _connections;
    private int _delivered;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        if (_server is not null)
            await Task.WhenAny(_server, Task.Delay(2000));
    }

    /// <summary>
    /// Accepts connections one after another. <paramref name="closeAfterFirstMessage"/>: the first connection takes one
    /// message, then announces 421 and closes, as an idle timeout does. <paramref name="refuseFirstMail"/>: the first
    /// connection answers MAIL FROM with 421.
    /// </summary>
    private int Start(bool closeAfterFirstMessage = false, bool refuseFirstMail = false)
    {
        _listener.Start();
        _server = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient socket;
                try { socket = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch { return; }
                var number = Interlocked.Increment(ref _connections);
                await ServeAsync(socket, number == 1 && closeAfterFirstMessage, number == 1 && refuseFirstMail);
            }
        });
        return ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    private async Task ServeAsync(TcpClient socket, bool closeAfterFirstMessage, bool refuseMail)
    {
        using (socket)
        {
            var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 fake ESMTP");
            while (await reader.ReadLineAsync() is { } line)
            {
                var verb = line.Split(' ')[0].ToUpperInvariant();
                switch (verb)
                {
                    case "EHLO":
                        await writer.WriteLineAsync("250-fake");
                        await writer.WriteLineAsync("250 8BITMIME");
                        break;
                    case "MAIL" when refuseMail:
                        await writer.WriteLineAsync("421 4.4.2 Service shutting down and closing transmission channel");
                        return;
                    case "MAIL" or "RCPT" or "RSET" or "NOOP":
                        await writer.WriteLineAsync("250 OK");
                        break;
                    case "DATA":
                        await writer.WriteLineAsync("354 go ahead");
                        while (await reader.ReadLineAsync() is { } data && data != ".") { }
                        Interlocked.Increment(ref _delivered);
                        await writer.WriteLineAsync("250 queued");
                        if (closeAfterFirstMessage)
                        {
                            // The idle timeout: the server says goodbye on its own and closes.
                            await writer.WriteLineAsync("421 4.4.2 Service shutting down (socket timeout)");
                            return;
                        }
                        break;
                    case "QUIT":
                        await writer.WriteLineAsync("221 bye");
                        return;
                    default:
                        await writer.WriteLineAsync("250 OK");
                        break;
                }
            }
        }
    }

    private static async Task<SmtpProducer> Producer(int port)
    {
        var endpoint = (SmtpEndpoint)new SmtpComponent().CreateEndpoint(EndpointUriParser.Parse(
            $"smtp://127.0.0.1?port={port}&security=None&from=notify@example.org&to=notify@example.org"));
        var producer = (SmtpProducer)endpoint.CreateProducer();
        await producer.Start();
        return producer;
    }

    [Fact]
    public async Task A_kept_connection_the_server_closed_is_replaced_and_the_message_still_goes()
    {
        var producer = await Producer(Start(closeAfterFirstMessage: true));

        await producer.Process(new Exchange(new Message("first")));
        await Task.Delay(100); // the server has said 421 and closed by now
        var second = () => producer.Process(new Exchange(new Message("second")));

        await second.Should().NotThrowAsync("the message must not be lost to a connection the server closed while idle");
        _delivered.Should().Be(2);
        _connections.Should().Be(2);
        await producer.Stop();
    }

    [Fact]
    public async Task A_refusal_on_a_fresh_connection_is_not_repeated()
    {
        var producer = await Producer(Start(refuseFirstMail: true));

        var send = () => producer.Process(new Exchange(new Message("refused")));

        await send.Should().ThrowAsync<Exception>();
        _connections.Should().Be(1, "a fresh connection's answer is the server's, not a stale socket");
        _delivered.Should().Be(0);
    }
}
