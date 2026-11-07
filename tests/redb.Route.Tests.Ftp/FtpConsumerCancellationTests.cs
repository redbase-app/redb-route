using System.Text;
using FluentFTP;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Ftp;

namespace redb.Route.Tests.Ftp;

/// <summary>
/// A cancellation coming out of the route that is not the consumer's own stop — an HttpClient timeout comes as a
/// <see cref="TaskCanceledException"/> — is a failure of that file: it goes down the failure path, <c>moveFailed</c>
/// included. It used to escape the per-file handling and fail the whole poll, so the file stayed where it was.
/// Expects FTP at localhost:21 (testuser/secret).
/// </summary>
[Trait("Category", "Integration")]
public sealed class FtpConsumerCancellationTests
{
    private const string Host = "localhost";
    private const int Port = 21;
    private const string Username = "testuser";
    private const string Password = "secret";

    [Fact]
    public async Task A_cancellation_out_of_the_route_moves_the_file_to_moveFailed()
    {
        var dir = $"cancel-{Guid.NewGuid():N}";
        using var client = new AsyncFtpClient(Host, Username, Password, Port);
        await client.Connect();
        using (var content = new MemoryStream(Encoding.UTF8.GetBytes("payload")))
            await client.UploadStream(content, $"/{dir}/a.txt", FtpRemoteExists.Overwrite, true);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new TaskCanceledException("a call inside the route timed out")));
        var uri = EndpointUriParser.Parse(
            $"ftp:///{dir}?host={Host}&port={Port}&username={Username}&password={Password}" +
            "&delay=200&initialDelay=10&moveFailed=.failed");
        var consumer = ((FtpEndpoint)new FtpComponent().CreateEndpoint(uri)).CreateConsumer(processor);

        await consumer.Start();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !await client.FileExists($"/{dir}/.failed/a.txt"))
            await Task.Delay(200);
        await consumer.Stop();

        (await client.FileExists($"/{dir}/.failed/a.txt")).Should().BeTrue("the failed file goes down the failure path");
        (await client.FileExists($"/{dir}/a.txt")).Should().BeFalse();

        await client.DeleteFile($"/{dir}/.failed/a.txt");
        await client.DeleteDirectory($"/{dir}/.failed");
        await client.DeleteDirectory($"/{dir}");
    }
}
