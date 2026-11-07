using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.File;

namespace redb.Route.Tests.File;

/// <summary>
/// A file consumer and producer keep working after <c>Stop()</c> and <c>Start()</c> of the same route context: the stop
/// ends the polling of the cached endpoint, the start polls again, and the producer still writes.
/// </summary>
public sealed class FileRestartTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "redb-file-restart-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Uri(string directory, string options) =>
        "file:///" + Path.Combine(_root, directory).Replace("\\", "/").TrimStart('/') + "?" + options;

    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        condition().Should().BeTrue(because);
    }

    [Fact]
    public async Task Consumer_and_producer_work_after_the_context_is_stopped_and_started_again()
    {
        var inbox = Path.Combine(_root, "in");
        var outbox = Path.Combine(_root, "out");
        Directory.CreateDirectory(inbox);
        Directory.CreateDirectory(outbox);
        var read = new List<string>();

        await using var ctx = new RouteContext();
        ctx.AddComponent(new FileComponent());
        ctx.AddRoutes(r => r.From(Uri("in", "delay=100&initialDelay=10&delete=true"))
            .Process(e => { lock (read) read.Add(e.ExchangeId); }));
        ctx.AddRoutes(r => r.From("direct://write").To(Uri("out", "")));

        async Task CheckAsync(string payload, int round)
        {
            await System.IO.File.WriteAllTextAsync(Path.Combine(inbox, payload + ".txt"), payload);
            await WaitForAsync(() => { lock (read) return read.Count == round; }, "файловый приёмник после рестарта читает");

            var producer = ctx.GetEndpoint("direct://write").CreateProducer();
            await producer.Start();
            await producer.Process(new Exchange(new Message(payload)));
            Directory.GetFiles(outbox).Should().HaveCount(round, "файловая отправка после рестарта пишет");
        }

        await ctx.Start();
        await CheckAsync("first", 1);

        await ctx.Stop();
        await ctx.Start();
        await CheckAsync("second", 2);
    }
}
