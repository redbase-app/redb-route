using redb.Route.Core;
using redb.Route.Tests.Sql.E2E.Base;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.E2E.MsSql;

/// <summary>
/// SQL Server keeps <c>@</c> for itself: a declared variable and the argument names of <c>EXEC</c> reach the server as
/// written, next to a <c>:#name</c> parameter. With <c>@name</c> placeholders the connector took them for its own and failed
/// with "has no value".
/// </summary>
[Trait("Category", "Integration")]
[Trait("SqlProvider", "mssql")]
[Trait("SqlSuite", "Connector")]
public sealed class MsSqlParameterSyntaxE2ETests : ParameterSyntaxE2ETestsBase
{
    protected override SqlE2EProvider Provider { get; } = new MsSqlE2EProvider(xactAbortOn: false);

    [Theory]
    [InlineData("EXEC sp_executesql N'SELECT @p AS v', N'@p int', @p = :#value")]
    [InlineData("DECLARE @n int = :#value; SELECT @n AS v")]
    public async Task TsqlAtSyntax_ReachesTheServer(string sql)
    {
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, Db.CreateConnectionFactory(), sql, new() { ["outputType"] = "Scalar" });
        var exchange = new Exchange(new Message());
        exchange.In.Headers["value"] = 7;

        var thrown = await Outcome.Of(() => endpoint.CreateProducer().Process(exchange, CancellationToken.None));

        thrown.Should().BeNull(Outcome.Describe(thrown));
        Convert.ToInt32(exchange.In.Body).Should().Be(7);
    }
}
