using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Sql;
using redb.Route.Sql.Connection;

namespace redb.Route.Tests.Sql.E2E.Infrastructure;

/// <summary>
/// Builds a <c>sql:</c> endpoint the production way: component registered on a route context, connection factory
/// in the context registry, options bound from URI parameters.
/// </summary>
public static class SqlEndpointHarness
{
    /// <summary>Registry name of the data source.</summary>
    public const string DataSourceName = "e2e";

    /// <summary>Creates an Execute-mode endpoint for <paramref name="sql"/> with extra URI parameters.</summary>
    public static SqlEndpoint CreateEndpoint(
        RouteContext context, ISqlConnectionFactory factory, string sql, Dictionary<string, string>? extraParameters = null)
    {
        var component = new SqlComponent();
        context.AddComponent(component);
        context.AddToRegistry(DataSourceName, factory);

        var parameters = new Dictionary<string, string>
        {
            ["mode"] = "Execute",
            ["dataSource"] = DataSourceName,
        };
        if (extraParameters is not null)
            foreach (var (key, value) in extraParameters)
                parameters[key] = value;

        var uri = new EndpointUri("sql", sql, $"sql:{sql}", parameters);
        return (SqlEndpoint)component.CreateEndpoint(uri);
    }
}
