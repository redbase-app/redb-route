using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.File;
using redb.Route.GenericFile;

namespace redb.Route.Tests.File;

/// <summary>
/// The path filters over a real directory tree: <c>antInclude</c>/<c>antExclude</c> on the path
/// relative to the polled directory, <c>filterDirectory</c> deciding before a directory is walked
/// into, and a registry filter bean. The local listing walks the tree itself for this — the
/// framework's all-directories enumeration cannot be told to leave a directory alone.
/// </summary>
public class FilePathFilterTests : IDisposable
{
    private readonly string _root;

    public FilePathFilterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "redb-route-pathfilter-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private void Write(string relativePath, string content = "x")
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
    }

    private FileEndpoint Endpoint(Dictionary<string, string> parameters)
    {
        var component = new FileComponent();
        var path = "/" + _root.Replace("\\", "/");
        return (FileEndpoint)component.CreateEndpoint(new EndpointUri("file", path, $"file://{path}", parameters));
    }

    private static async Task<List<string>> Poll(FileEndpoint endpoint)
    {
        var seen = new List<string>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => seen.Add(x.Arg<IExchange>().In.Headers[FileHeaders.FileRelativePath]!.ToString()!
                .Replace('\\', '/')));

        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && seen.Count == 0)
            await Task.Delay(50);
        await Task.Delay(200);   // let a second file of the same poll arrive
        await consumer.Stop();
        return seen.OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    private void PartnerTree()
    {
        Write("TYPE_A/outbox/a.csv");
        Write("TYPE_B/outbox/b.csv");
        Write("TYPE_C/outbox/c.csv");
    }

    [Fact]
    public async Task AntInclude_takes_two_directories_out_of_three()
    {
        PartnerTree();

        var polled = await Poll(Endpoint(new()
        {
            ["recursive"] = "true",
            ["noop"] = "true",
            ["delay"] = "5000",
            ["antInclude"] = "TYPE_A/outbox/*.csv,TYPE_B/outbox/*.csv",
        }));

        polled.Should().Equal("TYPE_A/outbox/a.csv", "TYPE_B/outbox/b.csv");
    }

    [Fact]
    public async Task FilterDirectory_keeps_the_walk_out_of_a_directory()
    {
        PartnerTree();

        var polled = await Poll(Endpoint(new()
        {
            ["recursive"] = "true",
            ["noop"] = "true",
            ["delay"] = "5000",
            ["filterDirectory"] = "header.redbFile.Name != 'TYPE_C'",
        }));

        polled.Should().Equal("TYPE_A/outbox/a.csv", "TYPE_B/outbox/b.csv");
    }

    [Fact]
    public async Task A_filter_bean_named_in_the_uri_decides_for_files_and_for_directories()
    {
        PartnerTree();

        // The whole path the option takes in production: a name in the registry, resolved by the
        // component when the endpoint is created.
        await using var context = new RouteContext();
        context.AddToRegistry("onlyTypeA", new OnlyTypeA());
        context.AddComponent(new FileComponent());
        var path = "/" + _root.Replace("\\", "/");
        var endpoint = (FileEndpoint)context.GetEndpoint(
            $"file://{path}?recursive=true&noop=true&delay=5000&filter=%23onlyTypeA");

        var polled = await Poll(endpoint);

        polled.Should().Equal("TYPE_A/outbox/a.csv");
    }

    [Fact]
    public void A_filter_name_the_registry_does_not_hold_fails_when_the_endpoint_is_created()
    {
        using var context = new RouteContext();
        context.AddComponent(new FileComponent());
        var path = "/" + _root.Replace("\\", "/");

        var act = () => context.GetEndpoint($"file://{path}?filter=%23missing");

        act.Should().Throw<InvalidOperationException>().WithMessage("*missing*");
    }

    [Fact]
    public async Task MaxDepth_still_stops_at_its_level()
    {
        Write("top.csv");
        Write("one/mid.csv");
        Write("one/two/deep.csv");

        var polled = await Poll(Endpoint(new()
        {
            ["recursive"] = "true",
            ["noop"] = "true",
            ["delay"] = "5000",
            ["maxDepth"] = "1",
        }));

        // The rewritten walk stops descending instead of enumerating everything and discarding it;
        // what a poll returns must not have changed.
        polled.Should().Equal("one/mid.csv", "top.csv");
    }

    [Fact]
    public async Task What_a_path_filter_rejected_is_never_deleted()
    {
        PartnerTree();

        await Poll(Endpoint(new()
        {
            ["recursive"] = "true",
            ["delete"] = "true",
            ["delay"] = "5000",
            ["antInclude"] = "TYPE_A/**",
        }));

        System.IO.File.Exists(Path.Combine(_root, "TYPE_A", "outbox", "a.csv")).Should().BeFalse();
        System.IO.File.Exists(Path.Combine(_root, "TYPE_B", "outbox", "b.csv")).Should().BeTrue();
        System.IO.File.Exists(Path.Combine(_root, "TYPE_C", "outbox", "c.csv")).Should().BeTrue();
    }

    private sealed class OnlyTypeA : IGenericFileFilter
    {
        public bool Accept(GenericFileInfo file) => file.FullPath.Replace('\\', '/').Contains("/TYPE_A/", StringComparison.Ordinal);

        public bool AcceptDirectory(string fullPath, string relativePath)
        {
            var rel = relativePath.Replace('\\', '/');
            return rel.StartsWith("TYPE_A", StringComparison.Ordinal) || rel.Contains("outbox", StringComparison.Ordinal);
        }
    }
}
