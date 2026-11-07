using StackExchange.Redis;

namespace redb.Route.Tests.Redis;

/// <summary>
/// The one connection the tests use to set up and check data beside the connector. A multiplexer per call was never
/// disposed: the suite left hundreds of open connections behind, and on a slow Linux stand behind a TCP forwarder
/// their reads broke off mid-test.
/// </summary>
internal static class TestRedis
{
    private const string ConnectionString = "localhost:6379";

    private static readonly Lazy<Task<ConnectionMultiplexer>> Shared =
        new(() => ConnectionMultiplexer.ConnectAsync(ConnectionString));

    /// <summary>The database of the shared test connection.</summary>
    public static async Task<IDatabase> Database() => (await Shared.Value.ConfigureAwait(false)).GetDatabase();
}
