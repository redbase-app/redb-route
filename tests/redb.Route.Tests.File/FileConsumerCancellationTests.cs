using System.Collections.Concurrent;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.File;

namespace redb.Route.Tests.File;

/// <summary>
/// A cancellation coming out of the route that is not the consumer's own stop — an HttpClient timeout comes as a
/// <see cref="TaskCanceledException"/>, and an error handler passes a cancellation on rather than swallow it — is a
/// failure of that file, like any other exception: the failure path runs (the idempotent key is released, moveFailed on
/// the remote connectors) and the rest of the poll goes on. It used to escape the per-file handling and fail the whole
/// poll: the files after it waited for the next poll, the failure path did not run, and the poll counted as an error.
/// </summary>
public sealed class FileConsumerCancellationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "redb-cancel-" + Guid.NewGuid().ToString("N")[..12]);

    public FileConsumerCancellationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task A_cancellation_out_of_the_route_fails_the_file_releases_its_key_and_the_poll_goes_on()
    {
        System.IO.File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
        System.IO.File.WriteAllText(Path.Combine(_root, "b.txt"), "b");
        var attempts = new ConcurrentDictionary<string, int>();
        var order = new ConcurrentQueue<string>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()).Returns<Task>(ci =>
        {
            var name = ci.Arg<IExchange>().In.Headers[FileHeaders.FileName]!.ToString()!;
            order.Enqueue(name);
            return attempts.AddOrUpdate(name, 1, (_, n) => n + 1) == 1 && name == "a.txt"
                ? Task.FromException(new TaskCanceledException("a call inside the route timed out"))
                : Task.CompletedTask;
        });
        var path = "/" + _root.Replace("\\", "/");
        var endpoint = (FileEndpoint)new FileComponent().CreateEndpoint(new EndpointUri("file", path, $"file://{path}",
            new Dictionary<string, string>
            {
                ["delay"] = "100", ["initialDelay"] = "10", ["idempotent"] = "true", ["noop"] = "true", ["sortBy"] = "Name",
            }));
        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);

        await consumer.Start();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !(attempts.GetValueOrDefault("a.txt") >= 2 && attempts.ContainsKey("b.txt")))
            await Task.Delay(50);
        await consumer.Stop();

        attempts.GetValueOrDefault("a.txt").Should().BeGreaterThanOrEqualTo(2,
            "the failed file's idempotent key is released, so a later poll takes it again");
        attempts.GetValueOrDefault("b.txt").Should().Be(1, "b keeps its key");
        order.Take(3).Should().Equal(["a.txt", "b.txt", "a.txt"],
            "b is taken in the same poll as a's failure, not left for the next one: a single failed file does not end the poll");
    }
}
