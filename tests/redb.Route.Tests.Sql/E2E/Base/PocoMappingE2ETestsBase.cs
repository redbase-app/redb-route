using redb.Route.Sql.Mapping;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.E2E.Base;

/// <summary>
/// Mapping driver types into POCO properties on a real server: dates and times arrive as <see cref="DateTime"/>, and only a
/// value that carries its offset or is UTC may become a <see cref="DateTimeOffset"/>; a timestamp without a time zone is an
/// error that says what to do instead.
/// </summary>
public abstract class PocoMappingE2ETestsBase : IAsyncLifetime
{
    private SqlE2EDatabase? _db;

    /// <summary>The provider under test.</summary>
    protected abstract SqlE2EProvider Provider { get; }

    /// <summary>One column <c>at</c>: the instant 2024-01-02 03:04:05 +03:00, in the server's type that keeps the time zone.</summary>
    protected abstract string ZonedTimestampSql { get; }

    /// <summary>One column <c>at</c>: 2024-01-02 03:04:05 in the server's timestamp type without a time zone.</summary>
    protected abstract string UnzonedTimestampSql { get; }

    private SqlE2EDatabase Db => _db ?? throw new InvalidOperationException("The database is checked in InitializeAsync.");

    public enum Kind { None = 0, First = 1, Second = 2 }

    public sealed class Target
    {
        public DateTimeOffset At { get; set; }
        public DateOnly Day { get; set; }
        public Kind Kind { get; set; }
    }

    /// <inheritdoc />
    public async Task InitializeAsync() => _db = await SqlE2EDatabase.CreateAsync(Provider);

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        try
        {
            if (_db is not null)
                await _db.DisposeAsync();
        }
        finally
        {
            (Provider as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public async Task ZonedTimestamp_IntoDateTimeOffset_KeepsTheInstant()
    {
        var (target, thrown) = await MapAsync(ZonedTimestampSql);

        thrown.Should().BeNull(Outcome.Describe(thrown));
        target!.At.UtcDateTime.Should().Be(new DateTime(2024, 1, 2, 0, 4, 5, DateTimeKind.Utc));
    }

    [Fact]
    public async Task UnzonedTimestamp_IntoDateTimeOffset_FailsWithHint()
    {
        var (_, thrown) = await MapAsync(UnzonedTimestampSql);

        thrown.Should().BeAssignableTo<InvalidOperationException>(Outcome.Describe(thrown))
            .Which.Message.Should().Contain("at").And.Contain("At").And.Contain("DateTime",
                "the message tells to map a timestamp without a time zone to a DateTime property");
    }

    [Fact]
    public async Task DateColumn_IntoDateOnly_Mapped()
    {
        var (target, thrown) = await MapAsync("SELECT CAST('2024-01-02' AS date) AS day");

        thrown.Should().BeNull(Outcome.Describe(thrown));
        target!.Day.Should().Be(new DateOnly(2024, 1, 2));
    }

    [Fact]
    public async Task IntegerColumn_IntoEnum_Mapped()
    {
        var (target, thrown) = await MapAsync("SELECT 2 AS kind");

        thrown.Should().BeNull(Outcome.Describe(thrown));
        target!.Kind.Should().Be(Kind.Second);
    }

    private async Task<(Target? Target, Exception? Thrown)> MapAsync(string sql)
    {
        await using var connection = await Db.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();

        Target? target = null;
        var thrown = await Outcome.Of(() =>
        {
            target = new PocoRowMapper<Target>().Map(reader);
            return Task.CompletedTask;
        });
        return (target, thrown);
    }
}
