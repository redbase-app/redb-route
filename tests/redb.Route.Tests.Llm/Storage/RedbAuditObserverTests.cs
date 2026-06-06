using redb.Core;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Engine.Observability;
using redb.Route.Llm.Providers;
using redb.Route.Llm.Storage.Redb;
using redb.Route.Llm.Storage.Redb.Schemas;

namespace redb.Route.Tests.Llm.Storage;

/// <summary>
/// Integration tests for <see cref="RedbAuditObserver"/>. Persists one
/// <see cref="ToolAuditProps"/> row per <c>OnToolInvokedAsync</c> call with
/// outcome classification (success / error / denied / skipped).
/// </summary>
[Collection("PostgresPro")]
public sealed class RedbAuditObserverTests
{
    private readonly PostgresProFixture _fx;

    public RedbAuditObserverTests(PostgresProFixture fx) => _fx = fx;

    private static AgentToolInvocationContext MakeCtx(
        string convId, string toolName, string toolUseId,
        string? output = """{"ok":true}""", bool skipped = false, string? skipReason = null, Exception? ex = null)
        => new()
        {
            Run = new AgentRunContext
            {
                ConversationId = convId,
                FactoryName = "test",
                ProviderId = "test",
                ModelId = "test-model",
                ExchangeId = $"x-{Guid.NewGuid():N}"
            },
            Tool = new LlmToolCapability { Name = toolName, Description = "t", InputSchema = "{}" },
            InputJson = """{"in":1}""",
            OutputJson = output,
            ToolUseId = toolUseId,
            Duration = TimeSpan.FromMilliseconds(42),
            Skipped = skipped,
            SkipReason = skipReason,
            Exception = ex
        };

    private async Task<List<ToolAuditProps>> LoadAuditForConvAsync(string convId)
    {
        var rows = await _fx.Redb.Query<ToolAuditProps>()
            .Where(p => p.ConversationId == convId)
            .ToListAsync();
        return rows.Select(r => r.Props).ToList();
    }

    [Fact]
    public async Task OnToolInvoked_Success_PersistsRow()
    {
        var obs = new RedbAuditObserver(_fx.ScopeFactory);
        var convId = $"c-{Guid.NewGuid():N}";

        await obs.OnToolInvokedAsync(MakeCtx(convId, "http_fetch", "tu-1"));

        var rows = await LoadAuditForConvAsync(convId);
        rows.Should().HaveCount(1);
        rows[0].ConversationId.Should().Be(convId);
        rows[0].ToolName.Should().Be("http_fetch");
        rows[0].ToolUseId.Should().Be("tu-1");
        rows[0].Outcome.Should().Be("success");
        rows[0].DurationMs.Should().Be(42);
        rows[0].OutputJson.Should().Be("""{"ok":true}""");
        rows[0].ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task OnToolInvoked_Exception_PersistsErrorOutcome()
    {
        var obs = new RedbAuditObserver(_fx.ScopeFactory);
        var convId = $"c-{Guid.NewGuid():N}";

        await obs.OnToolInvokedAsync(MakeCtx(
            convId, "buggy", "tu-2",
            output: null, ex: new InvalidOperationException("boom")));

        var rows = await LoadAuditForConvAsync(convId);
        rows.Should().HaveCount(1);
        rows[0].Outcome.Should().Be("error");
        rows[0].ErrorMessage.Should().Be("boom");
    }

    [Fact]
    public async Task OnToolInvoked_DeniedSkip_ClassifiesAsDenied()
    {
        var obs = new RedbAuditObserver(_fx.ScopeFactory);
        var convId = $"c-{Guid.NewGuid():N}";

        await obs.OnToolInvokedAsync(MakeCtx(
            convId, "delete_user", "tu-3",
            skipped: true, skipReason: "approval denied: not allowed"));

        var rows = await LoadAuditForConvAsync(convId);
        rows.Should().HaveCount(1);
        rows[0].Outcome.Should().Be("denied");
        rows[0].SkipReason.Should().Contain("denied");
    }
}
