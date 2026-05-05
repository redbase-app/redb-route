using System.Data.Common;

namespace redb.Route.Sql.Connection;

/// <summary>
/// Default connection factory. Resolves <see cref="DbProviderFactory"/> from the provider
/// registry and creates connections from a connection string.
/// Supports optional read/write splitting and connection validation on borrow.
/// </summary>
public sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    private readonly SqlConnectionOptions _options;
    private readonly DbProviderFactory _providerFactory;

    /// <summary>Creates a connection factory from the given options.</summary>
    /// <param name="options">Connection configuration.</param>
    public SqlConnectionFactory(SqlConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;

        _providerFactory = options.ProviderFactory
            ?? ResolveProviderFactory(options.ProviderName);
    }

    /// <inheritdoc />
    public async Task<DbConnection> CreateConnectionAsync(bool readOnly = false, CancellationToken ct = default)
    {
        var connectionString = readOnly && !string.IsNullOrEmpty(_options.ReadConnectionString)
            ? _options.ReadConnectionString
            : _options.ConnectionString;

        var attempts = 0;
        var maxAttempts = _options.EnableRetryOnFailure ? _options.MaxRetries + 1 : 1;

        while (true)
        {
            attempts++;
            DbConnection? connection = null;
            try
            {
                connection = _providerFactory.CreateConnection()
                    ?? throw new InvalidOperationException(
                        $"DbProviderFactory ({_options.ProviderName}) returned null from CreateConnection().");

                connection.ConnectionString = connectionString;

                await connection.OpenAsync(ct).ConfigureAwait(false);

                if (_options.TestOnBorrow)
                {
                    await ValidateConnectionAsync(connection, ct).ConfigureAwait(false);
                }

                return connection;
            }
            catch (OperationCanceledException) { connection?.Dispose(); throw; }
            catch
            {
                connection?.Dispose();

                if (attempts >= maxAttempts)
                    throw;

                await Task.Delay(_options.RetryDelay, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task ValidateConnectionAsync(DbConnection connection, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = _options.ValidationQuery;
        cmd.CommandTimeout = _options.ValidationTimeout;
        await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    private static DbProviderFactory ResolveProviderFactory(string providerName)
    {
        if (string.IsNullOrEmpty(providerName))
            throw new ArgumentException(
                "Either ProviderFactory or ProviderName must be specified in SqlConnectionOptions.");

        if (!DbProviderFactories.TryGetFactory(providerName, out var factory))
            throw new InvalidOperationException(
                $"ADO.NET provider '{providerName}' is not registered. " +
                "Call DbProviderFactories.RegisterFactory() or set ProviderFactory explicitly.");

        return factory;
    }
}
