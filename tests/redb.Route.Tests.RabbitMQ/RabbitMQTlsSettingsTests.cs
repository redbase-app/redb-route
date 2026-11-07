using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using redb.Route.Core;
using redb.Route.RabbitMQ;

namespace redb.Route.Tests.RabbitMQ;

/// <summary>
/// The TLS and login settings are checked before a connection is attempted: a setting that would be ignored, a broken
/// TLS version, an unusable client certificate or two ways to log in fail at startup, naming what is wrong.
/// </summary>
public sealed class RabbitMQTlsSettingsTests
{
    [Theory]
    [InlineData("sslServerName=rmq.example", "sslServerName")]
    [InlineData("sslCaCertPath=ca.pem", "sslCaCertPath")]
    [InlineData("sslProtocols=Tls13", "sslProtocols")]
    [InlineData("revocationMode=Online", "revocationMode")]
    public void A_tls_setting_without_tls_is_refused(string setting, string named)
    {
        var act = () => Endpoint($"host=h&{setting}");

        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain(named).And.Contain("ssl=true");
    }

    [Fact]
    public void Broken_tls_versions_are_refused()
    {
        var act = () => Endpoint("host=h&ssl=true&sslProtocols=Tls11");

        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain("Tls12 and Tls13");
    }

    [Fact]
    public void Tls12_and_Tls13_are_taken_as_a_list()
    {
        var act = () => Endpoint("host=h&ssl=true&sslProtocols=Tls12,Tls13");

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("ssl=true&sslCertPassword=x", "sslCertPassword is given without sslCertPath")]
    [InlineData("ssl=true&revocationSoftFail=true", "revocationMode is NoCheck")]
    [InlineData("authMechanism=External", "needs ssl=true and a client certificate")]
    [InlineData("ssl=true&authMechanism=External", "needs ssl=true and a client certificate")]
    public void Contradicting_settings_are_refused(string query, string reason)
    {
        var act = () => Endpoint($"host=h&{query}");

        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain(reason);
    }

    [Fact]
    public void An_expired_client_certificate_is_refused_before_the_handshake()
    {
        using var expired = TestCertificates.Client(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-1));
        var factory = new RabbitMQConnectionFactory { Host = "h", Ssl = true, ClientCertificate = expired };

        var act = () => factory.GetEndpoints();

        act.Should().Throw<CryptographicException>().Which.Message.Should().Contain("has expired");
    }

    [Fact]
    public void A_client_certificate_from_a_file_is_checked_the_same_way()
    {
        using var future = TestCertificates.ClientPfx(DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(30));
        var factory = new RabbitMQConnectionFactory { Host = "h", Ssl = true, SslCertPath = future.Path, SslCertPassword = TestCertificates.Passphrase };

        var act = () => factory.GetEndpoints();

        act.Should().Throw<CryptographicException>().Which.Message.Should().Contain("not yet valid");
    }

    [Fact]
    public void A_client_certificate_without_its_private_key_is_refused()
    {
        using var full = TestCertificates.Client(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
#if NET9_0_OR_GREATER
        using var publicOnly = X509CertificateLoader.LoadCertificate(full.RawData);
#else
        using var publicOnly = new X509Certificate2(full.RawData);
#endif
        var factory = new RabbitMQConnectionFactory { Host = "h", Ssl = true, ClientCertificate = publicOnly };

        var act = () => factory.GetEndpoints();

        act.Should().Throw<CryptographicException>().Which.Message.Should().Contain("no private key");
    }

    [Fact]
    public void The_client_certificate_object_and_the_allowed_versions_reach_every_host()
    {
        using var client = TestCertificates.Client(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var factory = new RabbitMQConnectionFactory
        {
            Host = "r1,r2", Ssl = true, ClientCertificate = client, SslProtocols = SslProtocols.Tls13,
        };

        factory.GetEndpoints().Should().OnlyContain(e =>
            e.Ssl.Certs != null && e.Ssl.Certs[0].GetCertHashString() == client.Thumbprint && e.Ssl.Version == SslProtocols.Tls13);
    }

    [Theory]
    [MemberData(nameof(RefusedFactories))]
    public void A_factory_is_checked_by_the_same_rules_and_for_its_login(string label, RabbitMQConnectionFactory factory, string reason)
    {
        var act = () => factory.Validate(label);

        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain(reason);
    }

    public static TheoryData<string, RabbitMQConnectionFactory, string> RefusedFactories() => new()
    {
        { "tls-off", new RabbitMQConnectionFactory { SslCaCertPath = "ca.pem" }, "ssl=true" },
        { "two-certs", new RabbitMQConnectionFactory { Ssl = true, SslCertPath = "c.pfx", ClientCertificate = NoKey() }, "once" },
        { "half-oauth", new RabbitMQConnectionFactory { OAuth2TokenEndpoint = "https://idp.example/token", OAuth2ClientId = "c" },
            "OAuth2TokenEndpoint, OAuth2ClientId and OAuth2ClientSecret together" },
        { "plain-http-idp", new RabbitMQConnectionFactory
            { OAuth2TokenEndpoint = "http://idp.example/token", OAuth2ClientId = "c", OAuth2ClientSecret = "s" }, "https" },
        { "two-logins", new RabbitMQConnectionFactory
            {
                OAuth2TokenEndpoint = "https://idp.example/token", OAuth2ClientId = "c", OAuth2ClientSecret = "s",
                Ssl = true, SslCertPath = "c.pfx", AuthMechanism = RabbitMQAuthMechanism.External,
            }, "choose one" },
    };

    [Fact]
    public void A_loopback_token_endpoint_may_be_plain_http()
    {
        var factory = new RabbitMQConnectionFactory
        {
            OAuth2TokenEndpoint = "http://localhost:9299/default/token", OAuth2ClientId = "c", OAuth2ClientSecret = "s",
        };

        var act = () => factory.Validate("local");

        act.Should().NotThrow();
    }

    private static X509Certificate2 NoKey()
    {
        using var full = TestCertificates.Client(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadCertificate(full.RawData);
#else
        return new X509Certificate2(full.RawData);
#endif
    }

    private static RabbitMQEndpoint Endpoint(string query)
        => (RabbitMQEndpoint)new RabbitMQComponent().CreateEndpoint(EndpointUriParser.Parse($"rabbitmq://q?{query}"));
}
