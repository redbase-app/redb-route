using redb.Route.Abstractions;
using redb.Route.As2;
using redb.Route.Core;
using As2Dsl = redb.Route.As2.Fluent.As2;

namespace redb.Route.Tests.As2;

/// <summary>
/// <c>headerFilterStrategy=#name</c> on AS2 endpoints: on a producer it decides which exchange headers go out as HTTP
/// headers of the message, on a consumer which request headers come in. The MIME, AS2, hop-by-hop and credential
/// fields and the consumer's redbAs2.* metadata stay outside the strategy.
/// </summary>
public sealed class As2HeaderFilterStrategyTests
{
    private static async Task<IReadOnlyDictionary<string, object?>> Send(string producerOptions, string consumerOptions,
        IHeaderFilterStrategy? everything = null)
    {
        var port = As2TestKit.FreePort();
        var received = new TaskCompletionSource<IExchange>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("me", As2TestKit.Receiver());
        context.AddToRegistry("them", As2TestKit.Sender());
        context.AddToRegistry("correlationOnly", new DefaultHeaderFilterStrategy { OutFilter = ["Correlation-ID"], FilterOnMatch = false });
        context.AddToRegistry("noSecrets", new DefaultHeaderFilterStrategy { InFilter = ["X-Secret"] });
        if (everything is not null)
            context.AddToRegistry("everything", everything);
        context.AddRoutes(r => r
            .From(As2Dsl.Receive("/inbound").Host("127.0.0.1").Port(port).ConnectionFactory("me").Build() + consumerOptions)
            .Process(e => received.TrySetResult(e)));
        await context.Start();

        var producer = context
            .GetEndpoint(As2Dsl.Send($"http://127.0.0.1:{port}/inbound").ConnectionFactory("them").Build() + producerOptions)
            .CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message("ISA*00*...EDI~") { ContentType = "application/edi-x12" });
        exchange.In.Headers["Correlation-ID"] = "c-42";
        exchange.In.Headers["X-Corr"] = "c1";
        exchange.In.Headers["X-Secret"] = "s1";
        exchange.In.Headers["Authorization"] = "Bearer secret";
        await producer.Process(exchange);
        await producer.Stop();

        return new Dictionary<string, object?>((await received.Task.WaitAsync(TimeSpan.FromSeconds(30))).In.Headers,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OnAProducer_AWhitelistSendsOnlyTheNamedHeader()
    {
        var headers = await Send("&headerFilterStrategy=#correlationOnly", "");

        headers["Correlation-ID"].Should().Be("c-42");
        headers.Should().NotContainKey("X-Corr");
        headers.Should().NotContainKey("X-Secret");
        headers.Should().ContainKey(As2Headers.SignatureValid, "the consumer's own metadata is set whatever the strategy says");
    }

    [Fact]
    public async Task OnAConsumer_TheStrategyDecidesWhatComesIn()
    {
        var headers = await Send("", "&headerFilterStrategy=#noSecrets");

        headers["X-Corr"].Should().Be("c1");
        headers.Should().NotContainKey("X-Secret", "the consumer's strategy drops it on the way in");
    }

    [Fact]
    public async Task ACredentialNeverCrosses_WhateverTheStrategySays()
    {
        var headers = await Send("&headerFilterStrategy=#everything", "&headerFilterStrategy=#everything",
            new DefaultHeaderFilterStrategy { FilterOnMatch = true });

        headers["X-Corr"].Should().Be("c1");
        headers.Should().NotContainKey("Authorization",
            "the producer never bridges another hop's credentials, and the consumer never takes the hop's own");
    }
}
