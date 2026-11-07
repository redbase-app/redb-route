using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Sqs;
using Message = redb.Route.Core.Message;
using SqsDsl = redb.Route.Sqs.Fluent.Sqs;

namespace redb.Route.Tests.Sqs;

/// <summary>
/// What crosses an SQS message today, producer to consumer: pinned before the header filter strategy
/// (docs/V4/headers/01-HEADER-FILTER-STRATEGY.md) takes the rule over, so the transport's default keeps it exactly.
/// The producer sends every exchange header but its own <c>redbSqs.*</c>/<c>redbSns.*</c> and null values as a string
/// message attribute (a <c>redbSqs.attr.X</c> header as attribute <c>X</c>). The consumer does not put attributes back
/// under their names: each comes as <c>redbSqs.attr.X</c>, so a received attribute cannot pose as a control header.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SqsHeaderMappingCharacterizationTests
{
    private static string Uri(string queue) => SqsDsl.Queue(queue).ServiceUrl("http://localhost:4566").Region("us-east-1")
        .Credentials("test", "test").AutoCreateQueue().Build();

    [Fact]
    public async Task ProducerToConsumer_TheHeadersThatCrossTheMessage()
    {
        var queue = $"hdr-char-{Guid.NewGuid():N}";
        var received = new TaskCompletionSource<IExchange>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var ctx = new RouteContext();
        ctx.AddComponent(new SqsComponent());
        ctx.AddRoutes(r => r.From(Uri(queue)).Process(e => received.TrySetResult(e)));
        await ctx.Start();

        var exchange = new Exchange(new Message("body"));
        exchange.In.Headers["X-Corr"] = "c1";
        exchange.In.Headers["X-Int"] = 42;
        exchange.In.Headers["X-Null"] = null;
        exchange.In.Headers["Authorization"] = "Bearer secret";
        exchange.In.Headers["redbHttp.Method"] = "DELETE";
        exchange.In.Headers[SqsHeaders.MessageAttributePrefix + "Named"] = "by-prefix";
        exchange.In.Headers[SqsHeaders.Prefix + "Custom"] = "own";
        var producer = ctx.GetEndpoint(Uri(queue)).CreateProducer();
        await producer.Start();
        await producer.Process(exchange);
        await producer.Stop();

        var headers = (await received.Task.WaitAsync(TimeSpan.FromSeconds(30))).In.Headers;

        const string attr = SqsHeaders.MessageAttributePrefix;
        headers[attr + "X-Corr"].Should().Be("c1");
        headers[attr + "X-Int"].Should().Be("42", "an attribute is sent as a String");
        headers.Should().NotContainKey(attr + "X-Null", "a null value is skipped");
        headers[attr + "Authorization"].Should().Be("Bearer secret", "nothing filters a credential on the way out");
        headers[attr + "redbHttp.Method"].Should().Be("DELETE", "it crosses, but only under the attribute prefix");
        headers.Should().NotContainKey("redbHttp.Method", "a received attribute never comes under its own name");
        headers.Should().NotContainKey("X-Corr");
        headers[attr + "Named"].Should().Be("by-prefix", "a redbSqs.attr.X header is sent as attribute X");
        headers.Should().NotContainKey(attr + SqsHeaders.Prefix + "Custom", "the producer keeps its own redbSqs.* back");
    }
}
