using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;
using redb.Route.Sql.Connection;

namespace redb.Route.Tests.Sql;

public class SqlConnectionTests : IDisposable
{
    private readonly SqliteTestHelper _db = new();

    // ── SqlConnectionOptions defaults ───────────────────────────────

    [Fact]
    public void SqlConnectionOptions_Defaults()
    {
        var opts = new SqlConnectionOptions();

        opts.ConnectionString.Should().BeEmpty();
        opts.ReadConnectionString.Should().BeNull();
        opts.ProviderName.Should().BeEmpty();
        opts.ProviderFactory.Should().BeNull();
        opts.MinPoolSize.Should().Be(0);
        opts.MaxPoolSize.Should().Be(100);
        opts.ConnectionLifetime.Should().Be(TimeSpan.Zero);
        opts.ConnectionIdleTimeout.Should().Be(TimeSpan.FromMinutes(5));
        opts.TestOnBorrow.Should().BeFalse();
        opts.ValidationQuery.Should().Be("SELECT 1");
        opts.ValidationTimeout.Should().Be(5);
        opts.ConnectTimeout.Should().Be(15);
        opts.CommandTimeout.Should().Be(30);
        opts.MaxRetries.Should().Be(0);
        opts.RetryDelay.Should().Be(TimeSpan.FromSeconds(1));
        opts.EnableRetryOnFailure.Should().BeFalse();
    }

    // ── SqliteConnectionFactory ─────────────────────────────────────

    [Fact]
    public async Task SqliteFactory_CreatesOpenConnection()
    {
        var factory = _db.CreateFactory();

        await using var conn = await factory.CreateConnectionAsync();

        conn.Should().NotBeNull();
        conn.State.Should().Be(System.Data.ConnectionState.Open);
    }

    [Fact]
    public async Task SqliteFactory_CanExecuteQuery()
    {
        var factory = _db.CreateFactory();

        await using var conn = await factory.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 42";
        var result = await cmd.ExecuteScalarAsync();

        result.Should().Be(42L);
    }

    [Fact]
    public async Task SqliteFactory_ReadOnlyParam_Ignored()
    {
        var factory = _db.CreateFactory();

        await using var conn1 = await factory.CreateConnectionAsync(readOnly: false);
        await using var conn2 = await factory.CreateConnectionAsync(readOnly: true);

        conn1.State.Should().Be(System.Data.ConnectionState.Open);
        conn2.State.Should().Be(System.Data.ConnectionState.Open);
    }

    // ── SqlConnectionFactory with SQLite ─────────────────────────────

    [Fact]
    public async Task SqlConnectionFactory_WithProviderFactory_CreatesConnection()
    {
        var options = new SqlConnectionOptions
        {
            ConnectionString = _db.ConnectionString,
            ProviderFactory = SqliteFactory.Instance
        };
        var factory = new SqlConnectionFactory(options);

        await using var conn = await factory.CreateConnectionAsync();

        conn.Should().NotBeNull();
        conn.State.Should().Be(System.Data.ConnectionState.Open);
    }

    [Fact]
    public async Task SqlConnectionFactory_ReadWriteSplit()
    {
        // Both strings point at same DB for testing, but validates the split logic works
        var options = new SqlConnectionOptions
        {
            ConnectionString = _db.ConnectionString,
            ReadConnectionString = _db.ConnectionString,
            ProviderFactory = SqliteFactory.Instance
        };
        var factory = new SqlConnectionFactory(options);

        await using var writeConn = await factory.CreateConnectionAsync(readOnly: false);
        await using var readConn = await factory.CreateConnectionAsync(readOnly: true);

        writeConn.State.Should().Be(System.Data.ConnectionState.Open);
        readConn.State.Should().Be(System.Data.ConnectionState.Open);
    }

    [Fact]
    public async Task SqlConnectionFactory_TestOnBorrow_Succeeds()
    {
        var options = new SqlConnectionOptions
        {
            ConnectionString = _db.ConnectionString,
            ProviderFactory = SqliteFactory.Instance,
            TestOnBorrow = true,
            ValidationQuery = "SELECT 1"
        };
        var factory = new SqlConnectionFactory(options);

        await using var conn = await factory.CreateConnectionAsync();

        conn.State.Should().Be(System.Data.ConnectionState.Open);
    }

    [Fact]
    public void SqlConnectionFactory_NoProviderOrFactoryString_Throws()
    {
        var options = new SqlConnectionOptions
        {
            ConnectionString = "something"
            // No ProviderName or ProviderFactory
        };

        var act = () => new SqlConnectionFactory(options);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SqlConnectionFactory_UnknownProviderName_Throws()
    {
        var options = new SqlConnectionOptions
        {
            ConnectionString = "something",
            ProviderName = "NonExistentProvider.12345"
        };

        var act = () => new SqlConnectionFactory(options);

        act.Should().Throw<InvalidOperationException>().WithMessage("*NonExistentProvider*");
    }

    // ── Retry logic ─────────────────────────────────────────────────

    [Fact]
    public async Task Retry_SucceedsAfterTransientFailure()
    {
        var failCount = 2;
        var factory = new SqlConnectionFactory(new SqlConnectionOptions
        {
            ProviderFactory = new CountingProviderFactory(failCount, _db.ConnectionString),
            ConnectionString = _db.ConnectionString,
            EnableRetryOnFailure = true,
            MaxRetries = 3,
            RetryDelay = TimeSpan.FromMilliseconds(10)
        });

        await using var conn = await factory.CreateConnectionAsync();
        conn.State.Should().Be(System.Data.ConnectionState.Open);
    }

    [Fact]
    public async Task Retry_ExhaustsAttemptsAndThrows()
    {
        var failCount = 5; // More than MaxRetries + 1
        var factory = new SqlConnectionFactory(new SqlConnectionOptions
        {
            ProviderFactory = new CountingProviderFactory(failCount, _db.ConnectionString),
            ConnectionString = _db.ConnectionString,
            EnableRetryOnFailure = true,
            MaxRetries = 2,
            RetryDelay = TimeSpan.FromMilliseconds(10)
        });

        var act = () => factory.CreateConnectionAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Simulated*");
    }

    [Fact]
    public async Task Retry_DisabledByDefault_FailsImmediately()
    {
        var factory = new SqlConnectionFactory(new SqlConnectionOptions
        {
            ProviderFactory = new CountingProviderFactory(1, _db.ConnectionString),
            ConnectionString = _db.ConnectionString,
            EnableRetryOnFailure = false // default
        });

        var act = () => factory.CreateConnectionAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Simulated*");
    }

    [Fact]
    public async Task Retry_CancellationRespected()
    {
        var factory = new SqlConnectionFactory(new SqlConnectionOptions
        {
            ProviderFactory = new CountingProviderFactory(10, _db.ConnectionString),
            ConnectionString = _db.ConnectionString,
            EnableRetryOnFailure = true,
            MaxRetries = 10,
            RetryDelay = TimeSpan.FromSeconds(10)
        });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => factory.CreateConnectionAsync(ct: cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── Helpers for retry tests ─────────────────────────────────────

    /// <summary>
    /// A DbProviderFactory that fails the first N calls to CreateConnection with a
    /// FailingConnection, then delegates to the real SQLite factory.
    /// </summary>
    private sealed class CountingProviderFactory : DbProviderFactory
    {
        private int _failsRemaining;
        private readonly string _realConnectionString;

        public CountingProviderFactory(int failCount, string realConnectionString)
        {
            _failsRemaining = failCount;
            _realConnectionString = realConnectionString;
        }

        public override DbConnection CreateConnection()
        {
            if (Interlocked.Decrement(ref _failsRemaining) >= 0)
                return new FailingConnection();
            var conn = Microsoft.Data.Sqlite.SqliteFactory.Instance.CreateConnection()!;
            return conn;
        }
    }

    /// <summary>
    /// A DbConnection stub that always throws on Open/OpenAsync.
    /// </summary>
    private sealed class FailingConnection : DbConnection
    {
        private string _connectionString = "";
        [AllowNull]
        public override string ConnectionString
        {
            get => _connectionString;
            set => _connectionString = value ?? "";
        }
        public override string Database => "";
        public override string DataSource => "";
        public override string ServerVersion => "";
        public override System.Data.ConnectionState State => System.Data.ConnectionState.Closed;

        public override void Open() =>
            throw new InvalidOperationException("Simulated transient failure");

        public override void Close() { }
        public override void ChangeDatabase(string databaseName) { }
        protected override DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel)
            => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand()
            => throw new NotSupportedException();
    }

    public void Dispose() => _db.Dispose();
}
