using FluentAssertions;
using redb.Route.Tests.Llm.TestHelpers;

namespace redb.Route.Tests.Llm.DslShowcase;

/// <summary>
/// Exercises the framework-backed utility tools shipped in <c>redb.Route.Llm.Tools</c>.
/// Each tool is a thin wrapper over a redb.Route primitive — <see cref="JsonPathTool"/>
/// over <c>JsonPathExpression</c>, <see cref="XPathTool"/> over <c>XPathExpression</c>,
/// <see cref="MathEvalTool"/> over <c>ExpressionResolver</c>, <see cref="RegexExtractTool"/>
/// over <c>System.Text.RegularExpressions.Regex</c> with a ReDoS guard.
/// <para>
/// The tools mount <c>direct:</c> routes via <c>.AsLlmTool(...)</c>, so we can drive them
/// straight through the route pipeline without involving an LLM provider.
/// </para>
/// </summary>
public sealed class UtilityToolTests
{
    [Fact]
    public async Task JsonPathTool_RegistersDescriptor_AndExtractsValue()
    {
        await using var host = LiveLlmHost.Build();
        await host.StartAsync(new JsonPathTool(new JsonPathOptions()));

        var descriptor = host.ToolRegistry.Get("json_path");
        descriptor.Should().NotBeNull();
        descriptor!.Capability.Safety.SideEffect.Should().Be(ToolSideEffect.ReadOnly);
        descriptor.Capability.Safety.Cost.Should().Be(ToolCostClass.Cheap);

        var input = """{"json":"{\"items\":[{\"name\":\"alpha\"},{\"name\":\"beta\"}]}","path":"$.items[1].name"}""";
        var ex = await host.SendAsync("direct:llm.json_path", input);
        ((string)ex.Out!.Body!).Should().Be("\"beta\"");
        ex.Out.Headers["llm.json_path.matched"].Should().Be(true);

        var miss = """{"json":"{\"x\":1}","path":"$.y"}""";
        var exMiss = await host.SendAsync("direct:llm.json_path", miss);
        ((string)exMiss.Out!.Body!).Should().Be("null");
        exMiss.Out.Headers["llm.json_path.matched"].Should().Be(false);
    }

    [Fact]
    public async Task JsonPathTool_SupportsRecursiveDescentAndFilters()
    {
        await using var host = LiveLlmHost.Build();
        await host.StartAsync(new JsonPathTool());

        // Recursive descent — pick all 'name' properties anywhere in the tree.
        var rec = """{"json":"{\"a\":{\"name\":\"x\"},\"b\":[{\"name\":\"y\"}]}","path":"$..name"}""";
        var exRec = await host.SendAsync("direct:llm.json_path", rec);
        ((string)exRec.Out!.Body!).Should().Be("[\"x\",\"y\"]");

        // Filter — items priced over 10.
        var filt = """{"json":"{\"items\":[{\"p\":5},{\"p\":12},{\"p\":20}]}","path":"$.items[?(@.p > 10)].p"}""";
        var exFilt = await host.SendAsync("direct:llm.json_path", filt);
        ((string)exFilt.Out!.Body!).Should().Be("[12,20]");
    }

    [Fact]
    public async Task XPathTool_ExtractsValueFromXmlDocument()
    {
        await using var host = LiveLlmHost.Build();
        await host.StartAsync(new XPathTool());

        host.ToolRegistry.Get("xpath").Should().NotBeNull();

        var input = """{"xml":"<lib><book><title>Dune</title></book><book><title>Foundation</title></book></lib>","xpath":"string(//book[1]/title)"}""";
        var ex = await host.SendAsync("direct:llm.xpath", input);
        ((string)ex.Out!.Body!).Should().Be("Dune");
        ex.Out.Headers["llm.xpath.matched"].Should().Be(true);

        var miss = """{"xml":"<r/>","xpath":"string(//missing)"}""";
        var exMiss = await host.SendAsync("direct:llm.xpath", miss);
        // XPath string() on no-match returns empty string, not null — but our tool
        // surfaces it as an empty string body (matched=true, value="").
        ((string)exMiss.Out!.Body!).Should().Be("");
    }

    [Fact]
    public async Task RegexExtractTool_ReturnsFirstAndAllMatches()
    {
        await using var host = LiveLlmHost.Build();
        await host.StartAsync(new RegexExtractTool(new RegexExtractOptions()));

        host.ToolRegistry.Get("regex_extract").Should().NotBeNull();

        var first = """{"text":"order 42 and 7","pattern":"\\d+"}""";
        var ex1 = await host.SendAsync("direct:llm.regex_extract", first);
        ((string)ex1.Out!.Body!).Should().Be("\"42\"");

        var all = """{"text":"order 42 and 7","pattern":"\\d+","all":true}""";
        var exAll = await host.SendAsync("direct:llm.regex_extract", all);
        ((string)exAll.Out!.Body!).Should().Be("[\"42\",\"7\"]");

        var named = """{"text":"name=alice","pattern":"name=(?<who>\\w+)","group":"who"}""";
        var exNamed = await host.SendAsync("direct:llm.regex_extract", named);
        ((string)exNamed.Out!.Body!).Should().Be("\"alice\"");

        var miss = """{"text":"abc","pattern":"\\d+"}""";
        var exMiss = await host.SendAsync("direct:llm.regex_extract", miss);
        ((string)exMiss.Out!.Body!).Should().Be("null");
    }

    [Fact]
    public async Task MathEvalTool_DelegatesToExpressionResolver()
    {
        await using var host = LiveLlmHost.Build();
        await host.StartAsync(new MathEvalTool(new MathEvalOptions()));

        host.ToolRegistry.Get("math_eval").Should().NotBeNull();

        async Task<string> EvalAsync(string expr)
        {
            var body = $"{{\"expression\":\"{expr}\"}}";
            var ex = await host.SendAsync("direct:llm.math_eval", body);
            return (string)ex.Out!.Body!;
        }

        (await EvalAsync("2 * (3 + 4)")).Should().Be("14");
        (await EvalAsync("100 / 4")).Should().Be("25");
        (await EvalAsync("42")).Should().Be("42");
        (await EvalAsync("'hello'")).Should().Be("\"hello\"");
    }
}
