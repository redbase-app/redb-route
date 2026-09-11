using FluentAssertions;
using redb.Route.As2;
using redb.Route.Core;
using Xunit;
using As2Dsl = redb.Route.As2.Fluent.As2;

namespace redb.Route.Tests.As2;

/// <summary>
/// Admission-limit options on the AS2 receiver (план HTTP_CONCURRENCY_LIMITS_PLAN, волна В2).
/// AS2 partners retry on their own MDN timeouts, so early shedding is cheaper than a saturated
/// receiver doing S/MIME work it will never finish. The shed mechanics are e2e-proved on the
/// shared host and the HTTP connector; here — that AS2 carries the quartet end to end.
/// </summary>
public sealed class As2ConcurrencyLimitOptionsTests
{
    [Fact]
    public void UriAndDsl_CarryTheOptions()
    {
        var built = As2Dsl.Receive("/as2/in")
            .Port(4080)
            .MaxConcurrentRequests(2, queue: 4)
            .RejectStatusCode(503)
            .RetryAfterSeconds(0)
            .Build();

        built.Should().Contain("maxConcurrentRequests=2")
            .And.Contain("requestQueueLimit=4")
            .And.Contain("rejectStatusCode=503")
            .And.Contain("retryAfterSeconds=0");

        var endpoint = (As2Endpoint)new As2Component().CreateEndpoint(EndpointUriParser.Parse(built));
        endpoint.EndpointOptions.MaxConcurrentRequests.Should().Be(2);
        endpoint.EndpointOptions.RequestQueueLimit.Should().Be(4);
        endpoint.EndpointOptions.RejectStatusCode.Should().Be(503);
        endpoint.EndpointOptions.RetryAfterSeconds.Should().Be(0);
    }

    [Fact]
    public void QueueWithoutLimit_FailsLoud()
    {
        var uri = EndpointUriParser.Parse("as2:/in?port=4082&requestQueueLimit=3");
        var act = () => new As2Component().CreateEndpoint(uri);
        act.Should().Throw<ArgumentException>().WithMessage("*requestQueueLimit*maxConcurrentRequests*");
    }
}
