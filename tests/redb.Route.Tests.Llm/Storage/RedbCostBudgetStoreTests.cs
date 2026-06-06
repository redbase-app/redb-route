using redb.Route.Llm.Engine.Governance;
using redb.Route.Llm.Storage.Redb;

namespace redb.Route.Tests.Llm.Storage;

/// <summary>Integration tests for <see cref="RedbCostBudgetStore"/>.</summary>
[Collection("PostgresPro")]
public sealed class RedbCostBudgetStoreTests
{
    private readonly PostgresProFixture _fx;

    public RedbCostBudgetStoreTests(PostgresProFixture fx) => _fx = fx;

    [Fact]
    public async Task GetUsage_Empty_ReturnsZero()
    {
        var store = new RedbCostBudgetStore(_fx.ScopeFactory);
        var usage = await store.GetUsageAsync($"c-{Guid.NewGuid():N}");
        usage.Should().Be(AgentUsage.Zero);
    }

    [Fact]
    public async Task Add_Accumulates_AcrossCalls()
    {
        var store = new RedbCostBudgetStore(_fx.ScopeFactory);
        var convId = $"c-{Guid.NewGuid():N}";

        var u1 = await store.AddAsync(convId, new AgentUsage(100, 50, 0.01m));
        u1.InputTokens.Should().Be(100);
        u1.OutputTokens.Should().Be(50);
        u1.CostUsd.Should().Be(0.01m);

        var u2 = await store.AddAsync(convId, new AgentUsage(30, 20, 0.005m));
        u2.InputTokens.Should().Be(130);
        u2.OutputTokens.Should().Be(70);
        u2.CostUsd.Should().Be(0.015m);

        var read = await store.GetUsageAsync(convId);
        read.InputTokens.Should().Be(130);
        read.OutputTokens.Should().Be(70);
        read.CostUsd.Should().Be(0.015m);
    }

    [Fact]
    public async Task Reset_RemovesAccumulator()
    {
        var store = new RedbCostBudgetStore(_fx.ScopeFactory);
        var convId = $"c-{Guid.NewGuid():N}";

        await store.AddAsync(convId, new AgentUsage(10, 5, 0.001m));
        (await store.GetUsageAsync(convId)).InputTokens.Should().Be(10);

        await store.ResetAsync(convId);
        (await store.GetUsageAsync(convId)).Should().Be(AgentUsage.Zero);
    }

    [Fact]
    public async Task Add_PerConversation_Isolated()
    {
        var store = new RedbCostBudgetStore(_fx.ScopeFactory);
        var c1 = $"c-{Guid.NewGuid():N}";
        var c2 = $"c-{Guid.NewGuid():N}";

        await store.AddAsync(c1, new AgentUsage(10, 0, 0m));
        await store.AddAsync(c2, new AgentUsage(99, 0, 0m));

        (await store.GetUsageAsync(c1)).InputTokens.Should().Be(10);
        (await store.GetUsageAsync(c2)).InputTokens.Should().Be(99);
    }
}
