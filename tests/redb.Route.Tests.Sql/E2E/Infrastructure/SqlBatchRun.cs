using System.Collections;
using redb.Route.Core;

namespace redb.Route.Tests.Sql.E2E.Infrastructure;

/// <summary>Runs the connector's batch once against an E2E table and captures the outcome for assertions.</summary>
public static class SqlBatchRun
{
    /// <summary>Produces <paramref name="items"/> through a fresh Execute endpoint; returns the exchange and what it threw.</summary>
    public static async Task<(Exchange Exchange, Exception? Thrown)> RunAsync(
        SqlE2EDatabase database, string sql, IList items, bool? breakOnError, int batchSize = 10,
        Dictionary<string, string>? extraParameters = null)
    {
        var parameters = new Dictionary<string, string>
        {
            ["outputType"] = "None",
            ["batchSize"] = batchSize.ToString(),
        };
        if (breakOnError is { } mode)
            parameters["breakBatchOnError"] = mode ? "true" : "false";
        if (extraParameters is not null)
            foreach (var (key, value) in extraParameters)
                parameters[key] = value;

        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, database.CreateConnectionFactory(), sql, parameters);
        var producer = endpoint.CreateProducer();
        var exchange = new Exchange(new Message(items));

        var thrown = await Outcome.Of(() => producer.Process(exchange, CancellationToken.None));
        return (exchange, thrown);
    }

    /// <summary>
    /// Indexes carried by the <c>redbSql.batchErrors</c> header, read by reflection so the tests compile against code that
    /// does not have the header's element type yet.
    /// </summary>
    public static IReadOnlyList<int> BatchErrorIndexes(Exchange exchange)
    {
        if (!exchange.In.Headers.TryGetValue("redbSql.batchErrors", out var header) || header is not IEnumerable errors)
            return [];
        return errors.Cast<object>()
            .Select(e => Convert.ToInt32(e.GetType().GetProperty("Index")?.GetValue(e)))
            .ToList();
    }
}
