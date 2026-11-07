using Microsoft.Extensions.DependencyInjection;

namespace redb.Route.Tests.Core.Integration;

[Collection("MsSql")]
public sealed class MsSqlIdempotentRepositoryTests : RedbIdempotentRepositoryIntegrationTests
{
    public MsSqlIdempotentRepositoryTests(MsSqlFixture fixture)
        : base(fixture.Redb, fixture.ServiceProvider.GetRequiredService<IServiceScopeFactory>()) { }
}
