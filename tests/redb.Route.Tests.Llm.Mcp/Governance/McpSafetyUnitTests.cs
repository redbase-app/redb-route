using System.Text.RegularExpressions;

namespace redb.Route.Tests.Llm.Mcp.Governance;

/// <summary>
/// Unit-level locks for the MCP safety surface. There is <b>no</b> local fake MCP server in this
/// repository (only live Serena tests under <c>SerenaFact</c>), so MCP behaviour is pinned where it can
/// be pinned without a server: on the descriptor, the override matcher and the configuration defaults.
/// </summary>
[Trait("Category", "Governance")]
public sealed class McpSafetyUnitTests
{
    private static McpSafetyOverride Override(string pattern) => new()
    {
        ToolNamePattern = pattern,
        Safety = new LlmToolSafety { RequiresApproval = true, SideEffect = ToolSideEffect.External }
    };

    /// <summary>
    /// The trap from <c>docs/V4/llm/00-INVENTORY.md</c> §1.6 п.28: <c>McpSafetyOverride.ToolNamePattern</c>
    /// is matched against the <b>raw</b> server-side tool name, while the operator and the model see the
    /// sanitized <c>{server}__{tool}</c> form. Copying the model-facing name into the pattern therefore
    /// matches nothing — silently, falling back to <c>DefaultSafety</c>.
    /// </summary>
    [Fact]
    public void McpSafetyOverride_ModelFacingPattern_DoesNotMatchRawName()
    {
        var modelFacing = McpToolDescriptor.BuildModelFacingName("serena", "find_symbol");
        modelFacing.Should().Be("serena__find_symbol", "the model-facing form is server__tool");

        var overridden = Override("^" + Regex.Escape(modelFacing) + "$");

        overridden.Matches("find_symbol").Should().BeFalse(
            "matching runs against the raw name, so a pattern built from the model-facing name silently misses");
    }

    /// <summary>
    /// The same trap, made loud: discovery reports every override that matched no discovered tool, so an
    /// operator sees the miss instead of quietly running on <c>DefaultSafety</c>.
    /// </summary>
    [Fact]
    public void McpSafetyOverride_ModelFacingPattern_IsReportedAsUnmatched()
    {
        var server = new McpServerOptions
        {
            Name = "serena",
            Transport = McpTransport.Stdio("mcp-server"),
            SafetyOverrides = [Override("^serena__find_symbol$")]
        };
        var tools = new List<ToolDefinition> { new() { Name = "find_symbol" } };

        McpDiscoveryService.FindUnmatchedOverrides(server, tools).Should().ContainSingle(
            "a pattern that matched nothing must be reported, not left for the operator to discover in production");
    }

    /// <summary>A pattern that does match the raw name is, of course, not reported.</summary>
    [Fact]
    public void McpSafetyOverride_RawNamePattern_IsNotReported()
    {
        var server = new McpServerOptions
        {
            Name = "serena",
            Transport = McpTransport.Stdio("mcp-server"),
            SafetyOverrides = [Override("^find_symbol$")]
        };
        var tools = new List<ToolDefinition> { new() { Name = "find_symbol" } };

        McpDiscoveryService.FindUnmatchedOverrides(server, tools).Should().BeEmpty();
    }

    /// <summary>
    /// A typo in a pattern used to surface only during discovery, where the exception turned into
    /// "the server is silently absent from the tool set". Compiling it at build time makes the typo
    /// fail where it is written.
    /// </summary>
    [Fact]
    public void McpSafetyOverride_InvalidRegex_FailsAtConstruction()
    {
        var act = () => Override("^find_symbol(");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*not a valid regex*",
                "the message must say which pattern is broken, not only that something is wrong");
    }

    /// <summary>
    /// Wave 6 deliberately flips the discovered-tool default: a third-party tool is unknown, so it is
    /// declared external <b>and</b> approval-required. Approval only becomes a control point with a gate
    /// other than the shipped <see cref="AutoApproveGate"/> — this test pins the declaration, and
    /// <c>ApprovalDefaultsTests.AddRedbRouteLlm_ResolvesAutoApproveGateByDefault</c> pins what that
    /// declaration currently buys.
    /// </summary>
    [Fact]
    public void McpServerOptions_DefaultSafety_RequiresApproval()
    {
        var options = new McpServerOptions
        {
            Name = "srv",
            Transport = McpTransport.Stdio("mcp-server")
        };

        options.DefaultSafety.SideEffect.Should().Be(ToolSideEffect.External);
        options.DefaultSafety.RequiresApproval.Should().BeTrue(
            "discovery cannot know what a third-party tool does, so the safe default is the declared one");
    }

    /// <summary>
    /// A caching policy on a discovered tool that is not read-only is refused when the server options are
    /// built — at configuration time, where the operator can still fix it, instead of during discovery,
    /// where it would read as "this server is silently missing from the tool set".
    /// </summary>
    [Fact]
    public void McpServerOptions_CachingOnNonReadOnlySafety_FailsAtConfiguration()
    {
        var builder = new McpServerOptionsBuilder
        {
            Name = "srv",
            Transport = McpTransport.Stdio("mcp-server"),
            DefaultSafety = new LlmToolSafety
            {
                SideEffect = ToolSideEffect.External,
                Caching = ToolCachingPolicy.Persist
            }
        };

        var act = () => builder.Build();

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("Only read-only tools may be cached");
    }
}
