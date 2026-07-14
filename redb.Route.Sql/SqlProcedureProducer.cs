using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Transactions;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Sql.Connection;
using redb.Route.Telemetry;

namespace redb.Route.Sql;

/// <summary>
/// Producer for SQL Procedure mode. Calls stored procedures or functions
/// with IN/OUT/INOUT parameters. OUT parameters are written to Exchange headers.
/// </summary>
/// <remarks>
/// <b>Transaction behavior:</b> always wraps execution in a local <see cref="System.Data.Common.DbTransaction"/>.
/// If an ambient <see cref="System.Transactions.Transaction.Current"/> exists (e.g. from
/// <c>BeginTransaction()</c> DSL), the local transaction is skipped and the connection
/// auto-enlists in the ambient <see cref="System.Transactions.TransactionScope"/>.
/// </remarks>
internal sealed class SqlProcedureProducer : IProducer
{
    private readonly SqlEndpoint _endpoint;
    private readonly SqlEndpointOptions _options;
    private ILogger? _logger;

    internal SqlProcedureProducer(SqlEndpoint endpoint, SqlEndpointOptions options)
    {
        _endpoint = endpoint;
        _options = options;
        _logger = (endpoint.Component as ComponentBase)?.Logger;
    }

    /// <inheritdoc />
    public Task Start(CancellationToken ct = default)
    {
        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("SQL procedure producer started");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Stop(CancellationToken ct = default)
    {
        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("SQL procedure producer stopped");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        using var activity = RouteTelemetryExtensions.StartTransportSpan(
            "sql.procedure", ActivityKind.Client,
            "db.system", _endpoint.Component.Scheme,
            _endpoint.Uri.NormalizedKey,
            destination: _options.ProcedureName);

        if (_options.Noop)
        {
            exchange.In.Headers[SqlHeaders.StoredProcedure] = _options.ProcedureName;
            return;
        }

        var factory = ResolveConnectionFactory();
        var sw = Stopwatch.StartNew();

        await using var connection = await factory.CreateConnectionAsync(readOnly: false, ct).ConfigureAwait(false);

        var hasAmbientTx = Transaction.Current != null;
        DbTransaction? tx = null;
        if (!hasAmbientTx)
        {
            tx = _options.IsolationLevel.HasValue
                ? await connection.BeginTransactionAsync(_options.IsolationLevel.Value, ct).ConfigureAwait(false)
                : await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        }

        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandTimeout = _options.CommandTimeout;
            if (tx != null) cmd.Transaction = tx;

            var procedureName = _options.ProcedureName!;

            if (_options.AsFunction)
            {
                // Call as function: SELECT function_name(params)
                cmd.CommandType = CommandType.Text;
                cmd.CommandText = BuildFunctionCall(procedureName);
            }
            else
            {
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.CommandText = procedureName;
            }

            var outParams = BindProcedureParameters(cmd, exchange);

            if (_options.AsFunction)
            {
                var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (result == DBNull.Value) result = null;

                // outputHeader keeps the incoming body intact and parks the function result
                // in a header instead — the same contract as in SqlProducer.
                if (!string.IsNullOrEmpty(_options.OutputHeader))
                    exchange.In.Headers[_options.OutputHeader] = result;
                else
                    exchange.In.Body = result;
            }
            else
            {
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            sw.Stop();

            // Read OUT/INOUT parameter values back to headers
            foreach (var (paramName, dbParam) in outParams)
            {
                var value = dbParam.Value;
                if (value == DBNull.Value) value = null;
                exchange.In.Headers[paramName] = value;
            }

            // Set standard headers
            exchange.In.Headers[SqlHeaders.StoredProcedure] = procedureName;
            exchange.In.Headers[SqlHeaders.ExecutionTime] = sw.ElapsedMilliseconds;

            if (_options.DataSource != null)
                exchange.In.Headers[SqlHeaders.DataSource] = _options.DataSource;

            if (tx != null)
            {
                exchange.In.Headers[SqlHeaders.TransactionId] = tx.GetHashCode().ToString();
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "SQL procedure execution failed: procedure={Procedure}, dataSource={DataSource}",
                _options.ProcedureName, _options.DataSource);
            if (tx != null)
            {
                try { await tx.RollbackAsync(ct).ConfigureAwait(false); }
                catch (Exception rbEx) { _logger?.LogError(rbEx, "SQL procedure transaction rollback failed"); }
            }
            throw;
        }
        finally
        {
            if (tx != null)
                await tx.DisposeAsync().ConfigureAwait(false);
        }
    }

    private List<(string Name, DbParameter Param)> BindProcedureParameters(DbCommand cmd, IExchange exchange)
    {
        var outParams = new List<(string, DbParameter)>();
        var paramDefs = ParseProcedureParams();
        var explicitParams = _options.ExplicitParameters;

        foreach (var def in paramDefs)
        {
            var param = cmd.CreateParameter();
            param.ParameterName = def.Name;
            param.DbType = def.DbType;
            param.Direction = def.Direction switch
            {
                SqlParamDirection.In => ParameterDirection.Input,
                SqlParamDirection.Out => ParameterDirection.Output,
                SqlParamDirection.InOut => ParameterDirection.InputOutput,
                _ => ParameterDirection.Input
            };

            if (def.Direction is SqlParamDirection.In or SqlParamDirection.InOut)
            {
                // Resolve value: expression > explicit param > header > body
                if (def.Expression != null)
                {
                    param.Value = SqlProducer.ResolveParamValue(def.Expression, exchange);
                }
                else if (explicitParams.TryGetValue(def.Name, out var explicitVal))
                {
                    param.Value = SqlProducer.ResolveParamValue(explicitVal, exchange);
                }
                else if (exchange.In.Headers.TryGetValue(def.Name, out var headerVal))
                {
                    param.Value = headerVal ?? DBNull.Value;
                }
                else if (exchange.In.Body is Dictionary<string, object?> dict &&
                         dict.TryGetValue(def.Name, out var bodyVal))
                {
                    param.Value = bodyVal ?? DBNull.Value;
                }
                else
                {
                    param.Value = DBNull.Value;
                }
            }

            cmd.Parameters.Add(param);

            if (def.Direction is SqlParamDirection.Out or SqlParamDirection.InOut)
                outParams.Add((def.Name, param));
        }

        return outParams;
    }

    private List<ProcedureParamDef> ParseProcedureParams()
    {
        var result = new List<ProcedureParamDef>();
        if (string.IsNullOrEmpty(_options.ProcedureParams))
            return result;

        // Format: "IN:name:DbType,OUT:name:DbType,INOUT:name:DbType:expression"
        var parts = _options.ProcedureParams.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var segments = part.Split(':', 4);
            if (segments.Length < 3) continue;

            var direction = Enum.Parse<SqlParamDirection>(segments[0], ignoreCase: true);
            var name = segments[1];
            var dbType = Enum.Parse<DbType>(segments[2], ignoreCase: true);
            var expression = segments.Length > 3 ? segments[3] : null;

            result.Add(new ProcedureParamDef(direction, name, dbType, expression));
        }

        return result;
    }

    private string BuildFunctionCall(string functionName)
    {
        var paramDefs = ParseProcedureParams();
        var paramList = string.Join(", ", paramDefs
            .Where(p => p.Direction is SqlParamDirection.In or SqlParamDirection.InOut)
            .Select(p => $"@{p.Name}"));

        return $"SELECT {functionName}({paramList})";
    }

    private ISqlConnectionFactory ResolveConnectionFactory() => _endpoint.ResolveConnectionFactory();

    private readonly record struct ProcedureParamDef(SqlParamDirection Direction, string Name, DbType DbType, string? Expression);
}
