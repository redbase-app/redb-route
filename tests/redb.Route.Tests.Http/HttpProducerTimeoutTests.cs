using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;
using redb.Route.Processors;

namespace redb.Route.Tests.Http;

/// <summary>
/// A timeout of the HTTP producer's own <see cref="System.Net.Http.HttpClient"/> — the receiver stayed
/// silent past <c>timeout=</c> — is a connection failure the route can handle, not a cancellation. It used
/// to surface as a bare <see cref="TaskCanceledException"/>, which every EIP handler treats as cancellation
/// and passes through, so neither <c>TryCatch/Catch</c> nor <c>OnException</c> could see it and the exchange
/// failed whole. A caller's cancellation must keep passing through untouched.
/// </summary>
[Collection("HttpServer")]
public class HttpProducerTimeoutTests : IAsyncLifetime
{
    private WebApplication? _server;
    private int _port;

    public async Task InitializeAsync()
    {
        _port = global::redb.Route.Tests.Shared.TestPorts.Next();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, _port));
        builder.Logging.ClearProviders();

        _server = builder.Build();
        // The receiver is alive but silent: it never answers, so only the client's timeout ends the call.
        _server.Map("/slow", async ctx =>
        {
            try { await Task.Delay(Timeout.Infinite, ctx.RequestAborted); }
            catch (OperationCanceledException) { /* the client timed out / was cancelled and left */ }
        });
        await _server.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.StopAsync();
            await _server.DisposeAsync();
        }
    }

    private HttpEndpoint Endpoint(int timeoutMs, string authority = "")
    {
        var component = new HttpComponent();
        var path = $"/{authority}localhost:{_port}/slow";
        var uri = new EndpointUri("http", path, $"http:{path}", new Dictionary<string, string>
        {
            ["method"] = "POST",
            ["timeout"] = timeoutMs.ToString(),
        });
        return (HttpEndpoint)component.CreateEndpoint(uri);
    }

    private static Exchange CreateExchange(string body) => new(new Message(body));

    [Fact]
    public async Task ProducerTimeout_IsCaughtByCatchTimeoutException_WithTaskCanceledInner()
    {
        var endpoint = Endpoint(timeoutMs: 300);
        var producer = (HttpProducer)endpoint.CreateProducer();
        await producer.Start();

        Exception? caught = null;
        var tryCatch = new TryCatchProcessor(producer)
            .Catch<TimeoutException>(new DelegateProcessor(ex => caught = ex.Exception));

        var exchange = CreateExchange("body");
        await tryCatch.Process(exchange);

        caught.Should().BeOfType<TimeoutException>(
            "a client timeout reaches Catch<TimeoutException> instead of passing through as cancellation");
        caught!.InnerException.Should().BeOfType<TaskCanceledException>(
            "the original HttpClient timeout is kept as the cause");
        exchange.ExceptionHandled.Should().BeTrue();

        await producer.Stop();
    }

    [Fact]
    public async Task ProducerTimeout_IsHandledByOnException()
    {
        var endpoint = Endpoint(timeoutMs: 300);
        var producer = (HttpProducer)endpoint.CreateProducer();
        await producer.Start();

        var handled = false;
        var onException = new OnExceptionProcessor(producer)
            .Handle<TimeoutException>(new DelegateProcessor(_ => handled = true), handled: true);

        var exchange = CreateExchange("body");
        await onException.Process(exchange);

        handled.Should().BeTrue("OnException<TimeoutException>().Handled(true) sees the producer timeout");
        exchange.ExceptionHandled.Should().BeTrue();

        await producer.Stop();
    }

    [Fact]
    public async Task CallerCancellation_StaysOperationCanceled_AndBypassesEveryHandler()
    {
        var endpoint = Endpoint(timeoutMs: 30_000); // a long timeout: the caller cancels first
        var producer = (HttpProducer)endpoint.CreateProducer();
        await producer.Start();

        var handled = false;
        var tryCatch = new TryCatchProcessor(producer)
            .Catch<TimeoutException>(new DelegateProcessor(_ => handled = true));
        var onException = new OnExceptionProcessor(tryCatch)
            .Handle<TimeoutException>(new DelegateProcessor(_ => handled = true), handled: true);

        using var cts = new CancellationTokenSource();
        var exchange = CreateExchange("body");
        var task = onException.Process(exchange, cts.Token);
        cts.CancelAfter(150);

        Func<Task> act = () => task;
        await act.Should().ThrowAsync<OperationCanceledException>(
            "a caller's cancellation is not a failure the route handles");
        handled.Should().BeFalse("neither TryCatch nor OnException may swallow cancellation");
        exchange.ExceptionHandled.Should().BeFalse();

        await producer.Stop();
    }

    [Fact]
    public async Task ProducerTimeout_MessageCarriesSanitizedUrlOnly()
    {
        var endpoint = Endpoint(timeoutMs: 300, authority: "user:s3cr3t@");
        var producer = (HttpProducer)endpoint.CreateProducer();
        await producer.Start();

        const string secretBody = "card=4111111111111111";
        Exception? caught = null;
        var tryCatch = new TryCatchProcessor(producer)
            .Catch<TimeoutException>(new DelegateProcessor(ex => caught = ex.Exception));

        await tryCatch.Process(CreateExchange(secretBody));
        await producer.Stop();

        caught.Should().BeOfType<TimeoutException>();
        caught!.Message.Should().Contain("timed out after 300 ms");
        caught.Message.Should().NotContain("s3cr3t", "the userinfo password must be masked (BR-4)");
        caught.Message.Should().NotContain(secretBody, "the request body must not reach the error text (BR-4)");
    }
}
