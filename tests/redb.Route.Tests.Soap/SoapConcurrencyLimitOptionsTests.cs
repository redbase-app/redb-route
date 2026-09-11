using FluentAssertions;
using redb.Route.Core;
using redb.Route.Soap;
using redb.Route.Soap.Fluent;
using Xunit;

namespace redb.Route.Tests.Soap;

/// <summary>
/// Admission-limit options on the SOAP consumer (план HTTP_CONCURRENCY_LIMITS_PLAN, волна В2).
/// The shed mechanics live in the shared host and are e2e-proved there and on the HTTP
/// connector; here the contract is that SOAP carries the same quartet: URI → options → the
/// consumer's RegisterRoute call.
/// </summary>
public sealed class SoapConcurrencyLimitOptionsTests
{
    [Fact]
    public void UriAndDsl_CarryTheOptions()
    {
        var built = Route.Soap.Fluent.Soap.Listen("/svc/orders")
            .Port(4090)
            .MaxConcurrentRequests(3, queue: 9)
            .RejectStatusCode(503)
            .RetryAfterSeconds(2)
            .Build();

        built.Should().Contain("maxConcurrentRequests=3")
            .And.Contain("requestQueueLimit=9")
            .And.Contain("rejectStatusCode=503")
            .And.Contain("retryAfterSeconds=2");

        var endpoint = (SoapEndpoint)new SoapComponent().CreateEndpoint(EndpointUriParser.Parse(built));
        endpoint.SoapOptions.MaxConcurrentRequests.Should().Be(3);
        endpoint.SoapOptions.RequestQueueLimit.Should().Be(9);
        endpoint.SoapOptions.RejectStatusCode.Should().Be(503);
        endpoint.SoapOptions.RetryAfterSeconds.Should().Be(2);
    }

    [Fact]
    public void QueueWithoutLimit_FailsLoud()
    {
        var uri = EndpointUriParser.Parse("soap:/svc/x?host=127.0.0.1&port=4091&requestQueueLimit=5");
        var act = () => new SoapComponent().CreateEndpoint(uri);
        act.Should().Throw<ArgumentException>().WithMessage("*requestQueueLimit*maxConcurrentRequests*");
    }
}
