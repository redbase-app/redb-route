using redb.Route.GenericFile;

namespace redb.Route.Tests.GenericFile;

/// <summary>
/// Apache Camel parity for the file consumer's filters. <c>include</c>/<c>exclude</c> only ever see
/// the file name, so "take these three directories out of two hundred" had no answer: the route had
/// to poll the parent recursively and throw away what it did not want — after downloading it, and
/// after <c>delete</c>/<c>move</c> had already touched it. Camel answers with filters that see the
/// path: <c>antInclude</c>/<c>antExclude</c> over the path relative to the polled directory,
/// <c>filterDirectory</c> deciding before a directory is listed at all, <c>filterFile</c> over the
/// file, and <c>filter</c> naming a bean in the registry.
/// </summary>
public class FileFilterParityTests : GenericFileTestBase
{
    private static Dictionary<string, string> Params(params (string Key, string Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    private async Task<List<string>> PolledNames(Dictionary<string, string> parameters)
    {
        var (processor, _, exchanges) = Collector();
        var consumer = (TestFileConsumer)Endpoint(parameters).CreateConsumer(processor);
        await consumer.PollOnceAsync();
        return exchanges.Select(e => (string)e.In.Headers["testFile.AbsolutePath"]!).OrderBy(p => p).ToList();
    }

    private void ThreePartnerDirectories()
    {
        Ops.AddFile("/in/TYPE_A/outbox/a.csv", "a");
        Ops.AddFile("/in/TYPE_B/outbox/b.csv", "b");
        Ops.AddFile("/in/TYPE_C/outbox/c.csv", "c");
    }

    [Fact]
    public async Task AntInclude_picks_directories_out_of_the_tree()
    {
        ThreePartnerDirectories();

        var polled = await PolledNames(Params(
            ("recursive", "true"),
            ("antInclude", "TYPE_A/outbox/*,TYPE_B/outbox/*")));

        polled.Should().Equal("/in/TYPE_A/outbox/a.csv", "/in/TYPE_B/outbox/b.csv");
    }

    [Fact]
    public async Task AntExclude_drops_what_it_matches()
    {
        ThreePartnerDirectories();

        var polled = await PolledNames(Params(
            ("recursive", "true"),
            ("antExclude", "TYPE_C/**")));

        polled.Should().Equal("/in/TYPE_A/outbox/a.csv", "/in/TYPE_B/outbox/b.csv");
    }

    [Fact]
    public async Task A_single_star_stops_at_a_slash_and_a_double_star_does_not()
    {
        Ops.AddFile("/in/one/x.csv", "1");
        Ops.AddFile("/in/one/two/x.csv", "2");

        var single = await PolledNames(Params(("recursive", "true"), ("antInclude", "*/x.csv")));
        var doubled = await PolledNames(Params(("recursive", "true"), ("antInclude", "**/x.csv")));

        // This is exactly what the name-based glob could not express: its '*' became ".*" and
        // crossed the separator, so the two patterns meant the same thing.
        single.Should().Equal("/in/one/x.csv");
        doubled.Should().Equal("/in/one/two/x.csv", "/in/one/x.csv");
    }

    [Fact]
    public async Task Ant_patterns_are_case_sensitive_unless_told_otherwise()
    {
        Ops.AddFile("/in/Type_A/a.csv", "a");

        var sensitive = await PolledNames(Params(("recursive", "true"), ("antInclude", "type_a/*")));
        var insensitive = await PolledNames(Params(
            ("recursive", "true"), ("antInclude", "type_a/*"), ("antFilterCaseSensitive", "false")));

        sensitive.Should().BeEmpty();
        insensitive.Should().Equal("/in/Type_A/a.csv");
    }

    [Fact]
    public async Task FilterDirectory_decides_before_the_directory_is_listed()
    {
        ThreePartnerDirectories();

        var polled = await PolledNames(Params(
            ("recursive", "true"),
            ("filterDirectory", "header.testFile.Name == 'TYPE_A' or header.testFile.Name == 'outbox'")));

        polled.Should().Equal("/in/TYPE_A/outbox/a.csv");
        // The point of the option: two hundred directories are not two hundred listings.
        Ops.ListedDirectories.Should().NotContain("/in/TYPE_B").And.NotContain("/in/TYPE_C");
    }

    [Fact]
    public async Task FilterFile_is_a_condition_over_the_file()
    {
        Ops.AddFile("/in/small.csv", "1");
        Ops.AddFile("/in/big.csv", new string('x', 100));

        var polled = await PolledNames(Params(("filterFile", "header.testFile.Length > 10")));

        polled.Should().Equal("/in/big.csv");
    }

    [Fact]
    public async Task A_filter_bean_from_the_registry_decides_for_files_and_directories()
    {
        ThreePartnerDirectories();
        var filter = new OnlyTypeA();

        var (processor, _, exchanges) = Collector();
        var endpoint = Endpoint(Params(("recursive", "true")), filter: filter);
        var consumer = (TestFileConsumer)endpoint.CreateConsumer(processor);
        await consumer.PollOnceAsync();

        exchanges.Select(e => (string)e.In.Headers["testFile.AbsolutePath"]!)
            .Should().Equal("/in/TYPE_A/outbox/a.csv");
        Ops.ListedDirectories.Should().NotContain("/in/TYPE_B");
    }

    [Fact]
    public async Task What_a_filter_rejected_is_never_deleted()
    {
        ThreePartnerDirectories();

        await PolledNames(Params(
            ("recursive", "true"),
            ("antInclude", "TYPE_A/**"),
            ("delete", "true")));

        // The reason this work exists: polling the parent and sorting it out in the route deleted
        // the partner's files from the directories the route was never meant to read.
        Ops.HasFile("/in/TYPE_A/outbox/a.csv").Should().BeFalse("it was polled and consumed");
        Ops.HasFile("/in/TYPE_B/outbox/b.csv").Should().BeTrue();
        Ops.HasFile("/in/TYPE_C/outbox/c.csv").Should().BeTrue();
    }

    /// <summary>Registry bean: takes TYPE_A only, and says so for the directory as well.</summary>
    private sealed class OnlyTypeA : IGenericFileFilter
    {
        public bool Accept(GenericFileInfo file) => file.FullPath.Contains("/TYPE_A/", StringComparison.Ordinal);

        public bool AcceptDirectory(string fullPath, string relativePath)
            => relativePath.StartsWith("TYPE_A", StringComparison.Ordinal) || relativePath.Contains("outbox", StringComparison.Ordinal);
    }
}
