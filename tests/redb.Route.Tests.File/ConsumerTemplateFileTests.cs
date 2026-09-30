using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.File;

namespace redb.Route.Tests.File;

/// <summary>
/// ConsumerTemplate over a polled directory, as Camel's: a Receive hands over one file and the file
/// stays where it is until the caller says it is done with it (<c>DoneUoW</c>). Only then is it
/// deleted or moved. Files the caller has not received are never touched, and a template stopped
/// before <c>DoneUoW</c> leaves its file in place.
/// </summary>
public class ConsumerTemplateFileTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "redb-route-ct-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly RouteContext _context = new();
    private ConsumerTemplate _template = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _context.AddComponent(new FileComponent());
        _template = new ConsumerTemplate(_context);
        _template.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _template.Dispose();
        await _context.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Uri(string options = "delete=true") =>
        "file:///" + _root.Replace("\\", "/").TrimStart('/') + "?delay=10&" + options;

    private void Write(int count)
    {
        for (var i = 0; i < count; i++)
            System.IO.File.WriteAllText(Path.Combine(_root, $"f{i}.txt"), $"payload {i}");
    }

    private int FilesLeft() => Directory.GetFiles(_root).Length;

    private async Task<int> SettleAndCount()
    {
        await Task.Delay(300);
        return FilesLeft();
    }

    [Fact]
    public async Task One_receive_takes_one_file_and_leaves_the_others()
    {
        Write(5);

        var exchange = await _template.Receive(Uri(), TimeSpan.FromSeconds(5));
        await _template.DoneUoW(exchange!);

        (await SettleAndCount()).Should().Be(4, "a file the caller never received must not be deleted");
    }

    [Fact]
    public async Task The_file_stays_until_the_caller_is_done()
    {
        Write(1);

        var exchange = await _template.Receive(Uri(), TimeSpan.FromSeconds(5));
        exchange!.In.Body.Should().BeOfType<byte[]>();

        (await SettleAndCount()).Should().Be(1, "the caller has not said it is done");
        await _template.DoneUoW(exchange);
        (await SettleAndCount()).Should().Be(0);
    }

    [Fact]
    public async Task Receives_hand_over_every_file_once()
    {
        Write(3);
        var names = new List<string>();

        for (var i = 0; i < 3; i++)
        {
            var exchange = await _template.Receive(Uri(), TimeSpan.FromSeconds(5));
            names.Add(exchange!.In.Headers[FileHeaders.FileName]!.ToString()!);
            await _template.DoneUoW(exchange);
        }

        names.Should().BeEquivalentTo(["f0.txt", "f1.txt", "f2.txt"]);
        (await SettleAndCount()).Should().Be(0);
    }

    [Fact]
    public async Task A_failure_recorded_by_the_caller_leaves_the_file()
    {
        Write(1);

        var exchange = await _template.Receive(Uri(), TimeSpan.FromSeconds(5));
        exchange!.Exception = new InvalidOperationException("the caller could not handle it");
        await _template.DoneUoW(exchange);

        (await SettleAndCount()).Should().Be(1, "a failed unit of work is rolled back, not committed");
    }

    [Fact]
    public async Task Stopping_before_done_leaves_the_file()
    {
        Write(1);

        var exchange = await _template.Receive(Uri(), TimeSpan.FromSeconds(5));
        exchange.Should().NotBeNull();
        _template.Stop();

        (await SettleAndCount()).Should().Be(1);
        var act = () => _template.DoneUoW(exchange!);
        await act.Should().ThrowAsync<InvalidOperationException>("the unit of work was rolled back by the stop");
    }

    [Fact]
    public async Task ReceiveBody_is_done_by_itself()
    {
        Write(1);

        var body = await _template.ReceiveBody<byte[]>(Uri(), TimeSpan.FromSeconds(5));

        System.Text.Encoding.UTF8.GetString(body!).Should().Be("payload 0");
        (await SettleAndCount()).Should().Be(0);
    }

    [Fact]
    public async Task ReceiveBody_reads_a_streamed_file_before_it_is_committed()
    {
        Write(1);

        var body = await _template.ReceiveBody<object>(Uri("delete=true&streamBody=true"), TimeSpan.FromSeconds(5));

        var stream = body.Should().BeAssignableTo<Stream>().Subject;
        new StreamReader(stream).ReadToEnd().Should().Be("payload 0", "the body outlives the committed file");
    }

    [Fact]
    public async Task Done_on_an_exchange_the_template_did_not_hand_out_is_refused()
    {
        var act = () => _template.DoneUoW(new Exchange());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
