using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using redb.Core;
using redb.Core.Models.Configuration;
using redb.Core.Pro.Extensions;
using redb.Postgres.Pro.Extensions;
using redb.Route.RedbCore.Models;

namespace redb.Route.Tests.Core.Integration;

/// <summary>Quick diagnostic: verify Postgres DB has the scheme and can round-trip objects.</summary>
public sealed class PostgresDiagnosticTests
{
    [Fact]
    public async Task Postgres_SchemeExists_And_CanSaveQuery()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .Build();

        var cs = config.GetConnectionString("Postgres")!;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRedbPro(o => o
            .UsePostgres(cs)
            .Configure(c => c.EavSaveStrategy = EavSaveStrategy.DeleteInsert));

        await using var sp = services.BuildServiceProvider();
        var redb = sp.GetRequiredService<IRedbService>();

        try { await redb.InitializeAsync(ensureCreated: true); }
        catch { await redb.InitializeAsync(); }

        var scheme = await redb.SyncSchemeAsync<IdempotentEntryProps>();
        scheme.Should().NotBeNull();

        // Unique key per test run to avoid collisions between parallel TFM runners
        var uid = Guid.NewGuid().ToString("N")[..8];
        var procName = $"diag-{uid}";

        // Save one object
        var obj = new redb.Core.Models.Entities.RedbObject<IdempotentEntryProps>
        {
            name = $"{procName}:key1",
            Props = new IdempotentEntryProps
            {
                ProcessorName = procName,
                MessageKey = "key1",
                CreatedAt = DateTimeOffset.UtcNow,
                Confirmed = false
            }
        };
        await redb.SaveAsync(obj);

        // Query it back
        var items = await redb.Query<IdempotentEntryProps>()
            .Where(e => e.ProcessorName == procName && e.MessageKey == "key1")
            .ToListAsync();

        items.Should().ContainSingle();
        items[0].Props.MessageKey.Should().Be("key1");

        // Cleanup
        await redb.DeleteAsync(items[0]);

        // Verify deleted
        var after = await redb.Query<IdempotentEntryProps>()
            .Where(e => e.ProcessorName == procName)
            .ToListAsync();
        after.Should().BeEmpty();
    }
}
