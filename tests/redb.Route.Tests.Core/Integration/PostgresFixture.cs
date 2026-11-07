using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using redb.Core;
using redb.Core.Models.Configuration;
using redb.Core.Pro.Extensions;
using redb.Postgres.Pro.Extensions;
using redb.Route.RedbCore.Models;

namespace redb.Route.Tests.Core.Integration;

/// <summary>
/// Shared xUnit fixture that boots a real Postgres-backed <see cref="IRedbService"/>,
/// creates the database schema and syncs the <see cref="IdempotentEntryProps"/> scheme.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public IRedbService Redb { get; private set; } = null!;
    public ServiceProvider ServiceProvider { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .Build();

        var connectionString = config.GetConnectionString("Postgres")!;
        var license = config["Redb:License"];

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRedbPro(options =>
        {
            options.UsePostgres(connectionString)
                .Configure(c =>
                {
                    c.PropsSaveStrategy = PropsSaveStrategy.DeleteInsert;
                });
            if (!string.IsNullOrWhiteSpace(license))
                options.WithLicense(license);
        });

        ServiceProvider = services.BuildServiceProvider();
        Redb = ServiceProvider.GetRequiredService<IRedbService>();

        try
        {
            await Redb.InitializeAsync(ensureCreated: true);
        }
        catch
        {
            // Schema may already exist from a parallel TFM run or previous session
            await Redb.InitializeAsync();
        }

        await Redb.SyncSchemeAsync<IdempotentEntryProps>();
    }

    public async Task DisposeAsync()
    {
        if (ServiceProvider is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else
            ServiceProvider?.Dispose();
    }
}

[CollectionDefinition("Postgres")]
public class PostgresCollection : ICollectionFixture<PostgresFixture>;
