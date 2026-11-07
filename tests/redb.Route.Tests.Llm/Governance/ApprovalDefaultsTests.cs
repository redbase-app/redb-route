using Microsoft.Extensions.DependencyInjection;
using redb.Route.Llm.Engine.Governance;

namespace redb.Route.Tests.Llm.Governance;

/// <summary>
/// Lock, not red. Records the <b>shipped default</b> that the governance docs talk about: a container
/// built by <c>AddRedbRouteLlm()</c> resolves <see cref="AutoApproveGate"/>, i.e. the approval flag
/// alone does not stop a tool call.
/// <para>
/// This stays true after wave 6 by decision: discovered MCP tools now declare
/// <c>RequiresApproval = true</c>, which with this gate produces an audit row
/// (<c>ApprovalId = "auto"</c>), not a blocked call. A host that wants approval to be a control point
/// registers its own gate — and <c>AddRedbLlmStorage()</c> leaves that registration alone.
/// </para>
/// </summary>
[Trait("Category", "Governance")]
public sealed class ApprovalDefaultsTests
{
    [Fact]
    public void AddRedbRouteLlm_ResolvesAutoApproveGateByDefault()
    {
        var services = new ServiceCollection();
        services.AddRedbRouteLlm();
        using var provider = services.BuildServiceProvider();

        var gate = provider.GetRequiredService<IApprovalGate>();

        gate.Should().BeOfType<AutoApproveGate>(
            "the shipped default gate auto-approves; a real control point requires replacing it");
    }
}
