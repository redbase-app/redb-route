using System.Net;
using System.Net.Sockets;
using redb.Route.Http;
using redb.Route.Tests.Llm.TestHelpers;

namespace redb.Route.Tests.Llm;

/// <summary>
/// <c>stream=body</c>: the agent run, tools and all, happens when the body is read — after the route, as a streamed SQL
/// result is read — and the text of every model call streams into <c>Out.Body</c> as the model writes it. The body
/// belongs to its exchange: it reads the exchange's resources, so it is read once, released with the exchange, refused
/// by <c>RequestBody</c> (which ends the exchange before it returns), and refused inside <c>.Transacted()</c>, whose block
/// would commit before the answer exists.
/// </summary>
public sealed class LazyStreamTests
{
    private const string Schema = """{"type":"object","properties":{"q":{"type":"string"}},"required":["q"]}""";
    private const string Input = """{"q":"x"}""";
    private const string Reply = """{"answer":"42"}""";

    private static FakeProvider ToolThenAnswer() => new FakeProvider()
        .EnqueueToolUse("lookup", Input, "tu_1")
        .EnqueueText("The answer is 42.");

    /// <summary>A one-call answer: for the tests about the body itself, where a tool would only add noise.</summary>
    private static FakeProvider PlainAnswer() => new FakeProvider().EnqueueText("The answer is 42.");

    private static LlmConnectionFactory Factory(ILlmProvider provider) =>
        new() { Provider = "stub", PrebuiltProvider = provider };

    private static async Task<LiveLlmHost> HostAsync(
        ILlmProvider provider, EchoToolRoute tool, bool transacted = false, string options = "&tools=lookup")
    {
        var host = LiveLlmHost.Build().AddFactory("demo", Factory(provider));
        var uri = $"llm://demo?stream=body&maxIterations=4{options}";
        await host.StartAsync(tool, r =>
        {
            var from = r.From("direct:agent");
            if (transacted) from.Transacted().To(uri);
            else from.To(uri);
        });
        return host;
    }

    private static EchoToolRoute Lookup() => new("lookup", "Look up a fact.", Schema, Reply);

    private static IAsyncEnumerable<string> BodyOf(IExchange exchange) =>
        (IAsyncEnumerable<string>)(exchange.Out?.Body ?? exchange.In.Body)!;

    private static async Task<List<string>> ReadAsync(IAsyncEnumerable<string> body)
    {
        var pieces = new List<string>();
        await foreach (var piece in body) pieces.Add(piece);
        return pieces;
    }

    [Fact]
    public async Task Body_WithTools_StreamsTheAnswerWhenRead()
    {
        var provider = new StreamingFakeProvider(ToolThenAnswer());
        var tool = Lookup();
        await using var host = await HostAsync(provider, tool);

        var exchange = await host.SendAsync("direct:agent", "What is the answer?");
        try
        {
            provider.StreamCalls.Should().Be(0, "nothing runs until the body is read");

            var pieces = await ReadAsync(BodyOf(exchange));

            string.Concat(pieces).Should().Be("The answer is 42.");
            pieces.Should().HaveCountGreaterThan(1, "the text streams as the model writes it");
            tool.CapturedInputs.Should().ContainSingle("the tool runs in the loop");
            provider.StreamCalls.Should().Be(2, "both model calls are streamed");
            exchange.Out!.Headers[LlmHeaders.ToolIterations].Should().Be(2, "the summary arrives after the run");
            exchange.Out.Headers[LlmHeaders.StopReason].Should().Be(nameof(LlmStopReason.EndTurn));
        }
        finally
        {
            await exchange.DisposeAsync();
        }
    }

    [Fact]
    public async Task Body_ThinkingStaysOutOfTheBody()
    {
        var script = new FakeProvider().Enqueue(new LlmResponse
        {
            Content = [new LlmThinkingBlock("Let me look it up."), new LlmTextBlock("The answer is 42.")],
            StopReason = LlmStopReason.EndTurn
        });
        await using var host = await HostAsync(new StreamingFakeProvider(script), Lookup(), options: "");

        var exchange = await host.SendAsync("direct:agent", "What is the answer?");
        try
        {
            string.Concat(await ReadAsync(BodyOf(exchange))).Should().Be("The answer is 42.",
                "thinking is not a reply and never reaches the body");
        }
        finally
        {
            await exchange.DisposeAsync();
        }
    }

    [Fact]
    public async Task Body_InsideTransacted_IsRefused()
    {
        var provider = new StreamingFakeProvider(PlainAnswer());
        await using var host = await HostAsync(provider, Lookup(), transacted: true, options: "");

        IExchange? exchange = null;
        var thrown = await Record.ExceptionAsync(async () => exchange = await host.SendAsync("direct:agent", "What is the answer?"));
        var failure = thrown ?? exchange?.Exception;
        if (exchange is not null) await exchange.DisposeAsync();

        failure.Should().NotBeNull(
            "the run would happen after the block committed, its tools outside the transaction the route declared");
        failure!.ToString().Should().Contain(".Transacted()", "the message says what the placement is wrong about")
            .And.Contain("stream=calls", "and names the mode that stays inside the transaction");
        provider.StreamCalls.Should().Be(0);
    }

    [Fact]
    public async Task Body_ReadAfterTheExchangeEnded_Fails()
    {
        var provider = new StreamingFakeProvider(PlainAnswer());
        await using var host = await HostAsync(provider, Lookup(), options: "");

        var exchange = await host.SendAsync("direct:agent", "What is the answer?");
        var body = BodyOf(exchange);
        await exchange.DisposeAsync();

        var thrown = await Record.ExceptionAsync(() => ReadAsync(body));

        thrown.Should().BeOfType<InvalidOperationException>("the run reads the exchange's resources, which are gone");
        provider.StreamCalls.Should().Be(0, "nothing ran for a body nobody can read");
    }

    [Fact]
    public async Task Body_ReadTwice_Fails()
    {
        await using var host = await HostAsync(new StreamingFakeProvider(PlainAnswer()), Lookup(), options: "");

        var exchange = await host.SendAsync("direct:agent", "What is the answer?");
        try
        {
            var body = BodyOf(exchange);
            await ReadAsync(body);

            var thrown = await Record.ExceptionAsync(() => ReadAsync(body));

            thrown.Should().BeOfType<InvalidOperationException>("one read is one agent run; a second read would run it again");
        }
        finally
        {
            await exchange.DisposeAsync();
        }
    }

    [Fact]
    public async Task Body_ThroughRequestBody_IsRefused()
    {
        await using var host = await HostAsync(new StreamingFakeProvider(PlainAnswer()), Lookup(), options: "");

        var thrown = await Record.ExceptionAsync(() => host.ProducerTemplate.RequestBody("direct:agent", "What is the answer?"));

        thrown.Should().BeOfType<InvalidOperationException>(
            "RequestBody ends the exchange before it returns, and this body reads the exchange's resources");
        thrown!.Message.Should().Contain("RequestAsync");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Fact]
    public async Task Body_OverHttp_IsServerSentEvents_WithTheSummaryLast()
    {
        var port = FreePort();
        var provider = new StreamingFakeProvider(ToolThenAnswer());
        var tool = Lookup();
        await using var host = LiveLlmHost.Build(httpHosting: new HttpHostingOptions(), httpPort: port)
            .AddFactory("demo", Factory(provider));
        await host.StartAsync(tool, r => r.From($"http:127.0.0.1:{port}/agent?inOut=true")
            .To("llm://demo?stream=body&tools=lookup&maxIterations=4"));

        using var http = new HttpClient();
        using var response = await http.PostAsync($"http://127.0.0.1:{port}/agent", new StringContent("What is the answer?"));
        var text = await response.Content.ReadAsStringAsync();

        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream",
            "the reply is a stream of events, whatever type the request carried");
        var data = text.Split('\n')
            .Where(l => l.StartsWith("data: ", StringComparison.Ordinal) && !l.Contains("llm.tool.iterations"))
            .Select(l => l["data: ".Length..]);
        string.Concat(data).Should().Be("The answer is 42.");
        text.Should().Contain("event: done", "the summary of the run comes last, after the text");
        // The HTTP consumer writes the summary values as strings; the value is what this test is about.
        text.Should().MatchRegex("\"llm\\.tool\\.iterations\":\"?2\"?", "two model calls: the tool call and the answer");
        tool.CapturedInputs.Should().ContainSingle();
    }
}
