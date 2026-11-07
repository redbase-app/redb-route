using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using redb.Route.Core;
using redb.Route.RabbitMQ;
using Xunit.Abstractions;

namespace redb.Route.Tests.RabbitMQ;

/// <summary>
/// TLS, mutual TLS, EXTERNAL and OAuth 2.0 against a real broker: the stand in <c>C:\Work\yaml\rabbit-tls</c>
/// (<c>docker compose up -d</c>; certificates from its <c>gen-certs.sh</c>, a test CA the operating system does not
/// trust). AMQPS on localhost:5671, plain AMQP on 5674, management on 15674 (admin/admin), token endpoint on 9299.
/// The broker's own view of each connection (user, login mechanism, TLS version) is read from its management API.
/// Override the certificate folder with REDB_RABBIT_TLS_CERTS.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RabbitMQTlsBrokerTests
{
    private static readonly string Certs =
        Environment.GetEnvironmentVariable("REDB_RABBIT_TLS_CERTS") ?? @"C:\Work\yaml\rabbit-tls\certs";

    private readonly ITestOutputHelper _output;

    public RabbitMQTlsBrokerTests(ITestOutputHelper output) => _output = output;

    private static string Cert(string file) => Path.Combine(Certs, file);

    private static string Tls(string extra = "") =>
        $"host=localhost&port=5671&ssl=true&sslCaCertPath={Cert("ca.pem")}{extra}";

    [Fact]
    public async Task A_private_ca_given_as_sslCaCertPath_is_trusted()
    {
        var name = Unique("ca");
        var seen = await SendAndInspectAsync($"{Tls()}&username=admin&password=admin&clientName={name}", name);
        seen.GetProperty("ssl").GetBoolean().Should().BeTrue();
        seen.GetProperty("user").GetString().Should().Be("admin");
    }

    [Fact]
    public async Task Without_the_ca_the_broker_certificate_is_rejected()
    {
        var act = () => SendAsync("host=localhost&port=5671&ssl=true&username=admin&password=admin");

        await act.Should().ThrowAsync<Exception>("the test CA is not in the system trust store");
    }

    [Fact]
    public async Task A_broker_certificate_from_another_ca_is_rejected()
    {
        var act = () => SendAsync($"host=localhost&port=5671&ssl=true&sslCaCertPath={Cert("other-ca.pem")}&username=admin&password=admin");

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task The_server_name_is_still_checked_when_the_ca_is_given()
    {
        var act = () => SendAsync($"{Tls("&sslServerName=rabbit.other.example")}&username=admin&password=admin");

        await act.Should().ThrowAsync<Exception>("the certificate names localhost, not rabbit.other.example");
    }

    [Fact]
    public async Task External_logs_in_with_the_client_certificate_and_no_password()
    {
        var name = Unique("external");
        var seen = await SendAndInspectAsync($"{Tls($"&sslCertPath={Cert("client.pfx")}&sslCertPassword=test")}&authMechanism=External&clientName={name}", name);
        seen.GetProperty("user").GetString().Should().Be("redb-client", "the broker takes the user from the certificate CN");
        seen.GetProperty("auth_mechanism").GetString().Should().Be("EXTERNAL");
    }

    [Fact]
    public async Task A_factory_takes_the_client_certificate_and_the_roots_as_objects()
    {
#if NET9_0_OR_GREATER
        using var client = X509CertificateLoader.LoadPkcs12FromFile(Cert("client.pfx"), "test");
#else
        using var client = new X509Certificate2(Cert("client.pfx"), "test");
#endif
        var roots = new X509Certificate2Collection();
        roots.ImportFromPemFile(Cert("ca.pem"));
        var name = Unique("objects");
        var factory = new RabbitMQConnectionFactory
        {
            Host = "localhost", Port = 5671, Ssl = true, ClientCertificate = client, SslCaCertificates = roots,
            AuthMechanism = RabbitMQAuthMechanism.External, ClientName = name, AutomaticRecovery = false,
        };

        (await SendAndInspectAsync("connectionFactory=objects", name, ("objects", factory))).GetProperty("user").GetString().Should().Be("redb-client");
    }

    [Theory]
    [InlineData("Tls12", "tlsv1.2")]
    [InlineData("Tls13", "tlsv1.3")]
    public async Task sslProtocols_limits_the_negotiated_version(string allowed, string negotiated)
    {
        var name = Unique(allowed);
        (await SendAndInspectAsync($"{Tls($"&sslProtocols={allowed}")}&username=admin&password=admin&clientName={name}", name)).GetProperty("ssl_protocol").GetString().Should().Be(negotiated);
    }

    [Fact]
    public async Task An_online_revocation_check_passes_a_chain_that_names_no_revocation_source()
    {
        // The stand's certificates carry no CRL distribution point and no AIA: there is nothing to check, so they
        // pass, as in the AS4 connector (Domibus).
        await SendAsync($"{Tls("&revocationMode=Online")}&username=admin&password=admin");
    }

    [Fact]
    public async Task OAuth2_logs_in_with_an_access_token()
    {
        var name = Unique("oauth2");
        var seen = await SendAndInspectAsync("connectionFactory=idp", name, ("idp", OAuth2Factory(name, automaticRecovery: true)));
        seen.GetProperty("user").GetString().Should().Be("redb-oauth-client", "the token's subject is the user");
    }

    [Fact]
    public async Task OAuth2_renews_the_token_on_the_open_connection_before_it_expires()
    {
        // The stand issues 60-second tokens and the broker closes a connection whose token expired. Recovery is off, so
        // only a renewal on the open connection (update-secret) keeps it alive past the first token's lifetime.
        var name = Unique("renew");
        var context = new RouteContext();
        var component = new RabbitMQComponent();
        context.AddComponent(component);
        context.AddToRegistry("idp", OAuth2Factory(name, automaticRecovery: false));
        var endpoint = (RabbitMQEndpoint)component.CreateEndpoint(EndpointUriParser.Parse($"rabbitmq://{Unique("q")}?connectionFactory=idp&declare=true"));
        var producer = (RabbitMQProducer)endpoint.CreateProducer();
        await producer.Start();
        var first = (await component.GetPooledConnectionsAsync()).Single();

        await Task.Delay(TimeSpan.FromSeconds(75));
        await producer.Process(new Exchange(new Message("after the first token expired")));

        var still = (await component.GetPooledConnectionsAsync()).Single();
        still.Should().BeSameAs(first);
        still.IsOpen.Should().BeTrue("the renewed token was put on the open connection");
        await producer.Stop();
        await endpoint.Stop();
        await context.DisposeAsync();
    }

    // ───── Helpers ─────

    private static RabbitMQConnectionFactory OAuth2Factory(string clientName, bool automaticRecovery) => new()
    {
        Host = "localhost", Port = 5674, ClientName = clientName, AutomaticRecovery = automaticRecovery,
        OAuth2TokenEndpoint = "http://localhost:9299/default/token", OAuth2ClientId = "redb", OAuth2ClientSecret = "secret",
    };

    private static string Unique(string what) => $"redb-tls-{what}-{Guid.NewGuid():N}";

    /// <summary>Sends one message as <see cref="SendAsync"/> does and reads the broker's record of the open connection.</summary>
    private async Task<JsonElement> SendAndInspectAsync(
        string query, string clientName, params (string Name, RabbitMQConnectionFactory Factory)[] factories)
    {
        JsonElement seen = default;
        await SendAsync(query, async () => seen = await BrokerConnectionAsync(clientName), factories);
        return seen;
    }

    private static Task SendAsync(string query, params (string Name, RabbitMQConnectionFactory Factory)[] factories)
        => SendAsync(query, null, factories);

    /// <summary>Opens a producer on the URI, sends one message to a queue it declares, and stops.</summary>
    private static async Task SendAsync(
        string query, Func<Task>? whileOpen, params (string Name, RabbitMQConnectionFactory Factory)[] factories)
    {
        var context = new RouteContext();
        var component = new RabbitMQComponent();
        context.AddComponent(component);
        foreach (var (name, factory) in factories)
            context.AddToRegistry(name, factory);

        var extra = query.Contains("connectionFactory=") ? string.Empty : "&automaticRecovery=false&connectionTimeout=10";
        var endpoint = (RabbitMQEndpoint)component.CreateEndpoint(
            EndpointUriParser.Parse($"rabbitmq://{Unique("q")}?{query}&declare=true&autoDelete=true{extra}"));
        var producer = (RabbitMQProducer)endpoint.CreateProducer();
        try
        {
            await producer.Start();
            await producer.Process(new Exchange(new Message("over tls")));
            if (whileOpen is not null)
                await whileOpen();
        }
        finally
        {
            await producer.Stop();
            await endpoint.Stop();
            await context.DisposeAsync();
        }
    }

    /// <summary>The broker's record of the connection named <paramref name="clientName"/>, from the management API.</summary>
    private async Task<JsonElement> BrokerConnectionAsync(string clientName)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes("admin:admin")));
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync("http://localhost:15674/api/connections"));
            foreach (var c in doc.RootElement.EnumerateArray())
                if (c.TryGetProperty("client_properties", out var props)
                    && props.TryGetProperty("connection_name", out var n) && n.GetString() == clientName)
                {
                    _output.WriteLine(c.ToString());
                    return c.Clone();
                }

            if (DateTime.UtcNow > deadline)
                throw new InvalidOperationException($"The broker lists no connection named '{clientName}'.");
            await Task.Delay(250);
        }
    }
}
