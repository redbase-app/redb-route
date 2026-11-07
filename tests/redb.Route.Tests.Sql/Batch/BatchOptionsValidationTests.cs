using redb.Route.Core;
using redb.Route.Tests.Sql.Batch.Fakes;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.Batch;

/// <summary>
/// Options that cannot take effect fail when the endpoint is created (decision 5-a, connector only). Today a
/// <c>${...}</c> value for a numeric option is silently dropped and the option keeps its default — for
/// <c>batchSize</c> that turns batching off and sends the whole list to a single statement.
/// </summary>
public sealed class BatchOptionsValidationTests
{
    [Theory]
    [InlineData("batchSize")]
    [InlineData("commandTimeout")]
    [InlineData("maxMessagesPerPoll")]
    public async Task KnownOption_UnconvertibleValue_FailsAtEndpointCreation(string option)
    {
        await using var context = new RouteContext();

        var act = () => SqlEndpointHarness.CreateEndpoint(context, new FixedConnectionFactory(new FakeBatchConnection()),
            "INSERT INTO t (id) VALUES (:#id)", new() { [option] = "${header.n}" });

        act.Should().Throw<ArgumentException>().WithMessage($"*{option}*");
    }

    [Fact]
    public async Task Batch_NegativeBatchSize_FailsAtEndpointCreation()
    {
        await using var context = new RouteContext();

        var act = () => SqlEndpointHarness.CreateEndpoint(context, new FixedConnectionFactory(new FakeBatchConnection()),
            "INSERT INTO t (id) VALUES (:#id)", new() { ["batchSize"] = "-1" });

        act.Should().Throw<ArgumentException>().WithMessage("*BatchSize*");
    }
}
