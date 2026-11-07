using Microsoft.Extensions.DependencyInjection;

namespace redb.Route.Tests.Core.Integration;

[Collection("Postgres")]
public sealed class PostgresIdempotentRepositoryTests : RedbIdempotentRepositoryIntegrationTests
{
    public PostgresIdempotentRepositoryTests(PostgresFixture fixture)
        : base(fixture.Redb, fixture.ServiceProvider.GetRequiredService<IServiceScopeFactory>()) { }
}
