using System.Collections.Concurrent;
using System.Text;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Processors;

/// <summary>
/// Integration tests for the streaming/large-data pipeline:
/// StreamCaching, StreamingSplitter (async), Tokenizers (Lines/Xml/Json)
/// through the DSL → Compiler → Engine chain.
/// </summary>
public class StreamingIntegrationTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    // ────────── StreamCaching ──────────

    [Fact]
    public async Task StreamCaching_InRoute_BodyRereadableAfterProcess()
    {
        var firstRead = string.Empty;
        var secondRead = string.Empty;

        _context.AddRoutes(r =>
        {
            r.From("direct://cache-in")
                .StreamCaching()
                .Process(e =>
                {
                    var stream = (Stream)e.In.Body!;
                    using var reader = new StreamReader(stream, leaveOpen: true);
                    firstRead = reader.ReadToEnd();
                    stream.Position = 0; // rewind — only possible because cached
                })
                .Process(e =>
                {
                    var stream = (Stream)e.In.Body!;
                    using var reader = new StreamReader(stream, leaveOpen: true);
                    secondRead = reader.ReadToEnd();
                });
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://cache-in").CreateProducer();
        await producer.Start();

        var body = new MemoryStream(Encoding.UTF8.GetBytes("cached-payload"));
        await producer.Process(new Exchange(new Message(body)));

        firstRead.Should().Be("cached-payload");
        secondRead.Should().Be("cached-payload");
    }

    // ────────── Streaming Split (IAsyncEnumerable) ──────────

    [Fact]
    public async Task StreamingSplit_ConfigureStyle_ProcessesAllParts()
    {
        var parts = new ConcurrentBag<string>();

        _context.AddRoutes(r =>
        {
            r.From("direct://stream-split")
                .Split(
                    ex => ToAsyncEnumerable("a", "b", "c"),
                    sub => sub.Process(e => parts.Add((string)e.In.Body!)));
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://stream-split").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("source")));

        parts.Should().HaveCount(3);
        parts.Should().Contain("a").And.Contain("b").And.Contain("c");
    }

    [Fact]
    public async Task StreamingSplit_FluentStyle_ProcessesAllParts()
    {
        var parts = new ConcurrentBag<string>();

        _context.AddRoutes(r =>
        {
            r.From("direct://stream-split-fluent")
                .Split(ex => ToAsyncEnumerable("x", "y"))
                    .Process(e => parts.Add((string)e.In.Body!))
                .EndSplit();
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://stream-split-fluent").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("source")));

        parts.Should().HaveCount(2);
        parts.Should().Contain("x").And.Contain("y");
    }

    [Fact]
    public async Task StreamingSplit_StopOnException_StopsEarly()
    {
        var processedParts = new ConcurrentBag<string>();

        _context.AddRoutes(r =>
        {
            r.From("direct://stream-split-stop")
                .Split(
                    ex => ToAsyncEnumerable("a", "b", "c", "d"),
                    sub => sub.Process(e =>
                    {
                        var body = (string)e.In.Body!;
                        processedParts.Add(body);
                        if (body == "b") throw new InvalidOperationException("fail on b");
                    }),
                    stopOnException: true);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://stream-split-stop").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("source"));

        // The exception propagates through the DirectProducer pipeline
        var act = async () => await producer.Process(exchange);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("fail on b");

        processedParts.Should().HaveCountLessThanOrEqualTo(2);
    }

    // ────────── StreamCaching + Streaming Split pipeline ──────────

    [Fact]
    public async Task StreamCaching_ThenStreamingSplit_WorksTogether()
    {
        var parts = new ConcurrentBag<string>();

        _context.AddRoutes(r =>
        {
            r.From("direct://cache-then-split")
                .StreamCaching()
                .ConvertBody<string>()
                .Split(
                    ex => ToAsyncEnumerable(
                        ((string)ex.In.Body!).Split('\n')),
                    sub => sub.Process(e => parts.Add((string)e.In.Body!)));
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://cache-then-split").CreateProducer();
        await producer.Start();

        var body = new MemoryStream(Encoding.UTF8.GetBytes("line1\nline2\nline3"));
        await producer.Process(new Exchange(new Message(body)));

        parts.Should().HaveCount(3);
        parts.Should().Contain("line1").And.Contain("line2").And.Contain("line3");
    }

    // ────────── ConvertBody + Stream ──────────

    [Fact]
    public async Task ConvertBody_StreamToString_InRoute()
    {
        var captured = string.Empty;

        _context.AddRoutes(r =>
        {
            r.From("direct://convert-stream")
                .ConvertBody<string>()
                .Process(e => captured = (string)e.In.Body!);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://convert-stream").CreateProducer();
        await producer.Start();

        var body = new MemoryStream(Encoding.UTF8.GetBytes("stream-data"));
        await producer.Process(new Exchange(new Message(body)));

        captured.Should().Be("stream-data");
    }

    [Fact]
    public async Task ConvertBody_StreamToByteArray_InRoute()
    {
        byte[]? captured = null;

        _context.AddRoutes(r =>
        {
            r.From("direct://convert-stream-bytes")
                .ConvertBody<byte[]>()
                .Process(e => captured = (byte[])e.In.Body!);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://convert-stream-bytes").CreateProducer();
        await producer.Start();

        var body = new MemoryStream(Encoding.UTF8.GetBytes("hello"));
        await producer.Process(new Exchange(new Message(body)));

        captured.Should().NotBeNull();
        Encoding.UTF8.GetString(captured!).Should().Be("hello");
    }

    // ────────── Exchange body dispose end-to-end ──────────

    [Fact]
    public async Task Exchange_DisposeAsync_DisposesStreamBody()
    {
        var stream = new MemoryStream(new byte[] { 1, 2, 3 });
        var exchange = new Exchange(new Message(stream));

        stream.CanRead.Should().BeTrue();
        await exchange.DisposeAsync();
        stream.CanRead.Should().BeFalse();
    }

    // ── Helper ──

    private static async IAsyncEnumerable<object?> ToAsyncEnumerable(params string[] items)
    {
        foreach (var item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }
}
