using redb.Route.Abstractions;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Engine.Governance;
using redb.Route.Llm.Storage.Redb;

namespace redb.Route.Tests.Llm.Storage;

/// <summary>Integration tests for <see cref="RedbApprovalStore"/>.</summary>
[Collection("PostgresPro")]
public sealed class RedbApprovalStoreTests
{
    private readonly PostgresProFixture _fx;

    public RedbApprovalStoreTests(PostgresProFixture fx) => _fx = fx;

    private static ApprovalRequest MakeRequest(string convId, string toolUseId, string toolName = "delete_user")
        => new()
        {
            ConversationId = convId,
            ToolUseId = toolUseId,
            InputJson = """{"id":42}""",
            Exchange = Substitute.For<IExchange>(),
            Tool = new LlmToolCapability
            {
                Name = toolName,
                Description = "test tool",
                InputSchema = "{}"
            }
        };

    [Fact]
    public async Task Record_Then_Find_Approved()
    {
        var store = new RedbApprovalStore(_fx.ScopeFactory);
        var convId = $"c-{Guid.NewGuid():N}";
        var approvalId = $"a-{Guid.NewGuid():N}";

        var req = MakeRequest(convId, "tu-1");
        await store.RecordAsync(req, ApprovalDecision.Approve(approvalId));

        var rec = await store.FindAsync(approvalId);
        rec.Should().NotBeNull();
        rec!.Approved.Should().BeTrue();
        rec.ConversationId.Should().Be(convId);
        rec.ToolName.Should().Be("delete_user");
        rec.InputJson.Should().Be("""{"id":42}""");
    }

    [Fact]
    public async Task Record_Denied_PreservesReason()
    {
        var store = new RedbApprovalStore(_fx.ScopeFactory);
        var approvalId = $"a-{Guid.NewGuid():N}";

        var req = MakeRequest($"c-{Guid.NewGuid():N}", "tu-2");
        var decision = new ApprovalDecision { Approved = false, ApprovalId = approvalId, Reason = "policy violation" };
        await store.RecordAsync(req, decision);

        var rec = await store.FindAsync(approvalId);
        rec.Should().NotBeNull();
        rec!.Approved.Should().BeFalse();
        rec.Reason.Should().Be("policy violation");
    }

    [Fact]
    public async Task Find_Unknown_ReturnsNull()
    {
        var store = new RedbApprovalStore(_fx.ScopeFactory);
        (await store.FindAsync("does-not-exist")).Should().BeNull();
    }
}
