using Amazon.Runtime;
using redb.Route.Core;
using redb.Route.S3;

namespace redb.Route.Tests.S3;

/// <summary>
/// Ф11 волна S-Б (finding S-5 of docs/V4/REVIEW-S3.md): connection options that were
/// declared, documented and bound — but never reached the AWS SDK config.
/// </summary>
public sealed class S3ConnectionConfigTests
{
    [Fact]
    public void Factory_SocketTimeout_ReachesSdkConfig()
    {
        var client = new S3ConnectionFactory
        {
            ServiceUrl = "http://localhost:9000",
            AccessKey = "k",
            SecretKey = "s",
            SocketTimeout = 12_345,
        }.Build();

        client.Config.Timeout.Should().Be(TimeSpan.FromMilliseconds(12_345),
            "SocketTimeout обязан доезжать до SDK как request timeout, а не быть мёртвой опцией");
    }

    [Fact]
    public void Factory_ConnectionTimeout_MapsToConnectTimeout()
    {
        var client = new S3ConnectionFactory
        {
            ServiceUrl = "http://localhost:9000",
            AccessKey = "k",
            SecretKey = "s",
            ConnectionTimeout = 7_000,
        }.Build();

        client.Config.ConnectTimeout.Should().Be(TimeSpan.FromMilliseconds(7_000));
    }

    [Fact]
    public void Factory_RetryModeAdaptive_ReachesSdkConfig()
    {
        var client = new S3ConnectionFactory
        {
            ServiceUrl = "http://localhost:9000",
            AccessKey = "k",
            SecretKey = "s",
            RetryMode = "adaptive",
        }.Build();

        client.Config.RetryMode.Should().Be(RequestRetryMode.Adaptive,
            "RetryMode обязан доезжать до SDK, а не быть мёртвой опцией");
    }

    [Fact]
    public void Factory_TrustAllCertificates_InstallsHttpClientFactory()
    {
        var client = new S3ConnectionFactory
        {
            ServiceUrl = "https://localhost:9000",
            AccessKey = "k",
            SecretKey = "s",
            TrustAllCertificates = true,
        }.Build();

        client.Config.HttpClientFactory.Should().NotBeNull(
            "TrustAllCertificates обязан ставить фабрику с отключённой валидацией сертификата");
    }

    [Fact]
    public async Task ConnectionFactoryTypo_FailsLoud_NeverFallsBackToUriParams()
    {
        // Ф11 волна Ж (Ж-1): опечатка в connectionFactory = молчаливое подключение
        // с URI-кредами — рабочая конфигурация и мисконфигурация неразличимы.
        await using var ctx = new RouteContext();
        var component = new S3Component();
        ctx.AddComponent(component);

        var endpoint = (S3Endpoint)ctx.GetEndpoint(
            "s3://bucket?connectionFactory=typo-name&serviceUrl=http://localhost:9000" +
            "&accessKey=k&secretKey=s");

        var act = () => endpoint.GetOrCreateClientAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("typo-name"),
                "заданное, но не найденное имя фабрики обязано падать громко");
    }

    [Fact]
    public async Task EndpointOptions_Trio_ReachesSdkConfig()
    {
        var uri = EndpointUriParser.Parse(
            "s3://bucket?serviceUrl=http://localhost:9000&accessKey=k&secretKey=s" +
            "&socketTimeout=23456&retryMode=adaptive&trustAllCertificates=true");
        var endpoint = (S3Endpoint)new S3Component().CreateEndpoint(uri);

        var client = await endpoint.GetOrCreateClientAsync();

        client.Config.Timeout.Should().Be(TimeSpan.FromMilliseconds(23_456));
        client.Config.RetryMode.Should().Be(RequestRetryMode.Adaptive);
        client.Config.HttpClientFactory.Should().NotBeNull();
    }
}
