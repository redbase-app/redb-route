using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using redb.Route.Core;
using redb.Route.Http;
using redb.Route.Processors;

namespace redb.Route.Tests.Http;

/// <summary>
/// <c>authScheme</c>, <c>username</c> and <c>password</c> are the credentials an <c>http:</c>
/// <b>producer</b> sends. They live in the options class the consumer shares, so
/// <c>from("http://0.0.0.0:8080/api?authScheme=basic&amp;username=admin&amp;password=…")</c> bound
/// without a word, passed validation — which even demanded a non-empty user and password — and
/// started a route that checked nothing. Its author believed the endpoint was closed, and the password
/// sat in the route key besides. Found while planning inbound authentication, 2026-09-25.
/// <para>
/// Refused now, until the consumer has a real inbound check of its own. Only parameters written on
/// the consumer's own URI count: a named connection factory may legitimately carry producer
/// credentials alongside the TLS material a consumer reuses it for.
/// </para>
/// </summary>
[Collection("HttpServer")]
public class HttpConsumerIgnoredAuthTests
{
    private static int FreePort() => global::redb.Route.Tests.Shared.TestPorts.Next();

    private static RouteContext Context()
    {
        var context = new RouteContext();
        context.AddComponent(new HttpComponent { ServerManager = new SharedHttpServerManager() });
        return context;
    }

    [Theory]
    [InlineData("authScheme=basic&username=admin&password=s3cret-value")]
    [InlineData("username=admin&password=s3cret-value")]
    [InlineData("authScheme=bearer")]
    [InlineData("authScheme=bearer&authToken=s3cret-value")]
    public async Task Inbound_credentials_on_a_consumer_are_refused_rather_than_ignored(string query)
    {
        await using var context = Context();
        var port = FreePort();

        var act = () => context.GetEndpoint($"http://127.0.0.1:{port}/api?{query}")
            .CreateConsumer(new DelegateProcessor(_ => { }));

        var error = act.Should().Throw<InvalidOperationException>().Which;
        error.Message.Should().Contain("does not check", "the reader must learn the endpoint is not protected");
        error.Message.Should().NotContain("s3cret-value", "a refusal must never echo a secret into a log");
    }

    [Fact]
    public async Task A_connection_factory_carrying_producer_credentials_does_not_block_a_consumer()
    {
        await using var context = Context();
        context.AddToRegistry("partner", new HttpConnectionFactory
        {
            AuthScheme = HttpAuthScheme.Basic,
            Username = "svc",
            Password = "factory-secret",
        });
        var port = FreePort();

        var act = () => context.GetEndpoint($"http://127.0.0.1:{port}/api?connectionFactory=partner")
            .CreateConsumer(new DelegateProcessor(_ => { }));

        act.Should().NotThrow("a factory is shared with producers; only what the consumer wrote itself is intent");
    }

    [Fact]
    public async Task The_producer_keeps_its_credentials()
    {
        await using var context = Context();

        var act = () => context.GetEndpoint("http://example.invalid/api?authScheme=basic&username=u&password=p")
            .CreateProducer();

        act.Should().NotThrow("these options are exactly what a producer is for");
    }
}
