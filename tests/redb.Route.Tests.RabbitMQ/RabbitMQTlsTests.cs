using System.Net;
using System.Net.Sockets;
using System.Text;
using redb.Route.Core;
using redb.Route.RabbitMQ;

namespace redb.Route.Tests.RabbitMQ;

/// <summary>
/// TLS on the wire. No broker is needed: a local TCP listener stands in for RabbitMQ and reads the first
/// bytes the client sends. A TLS connection starts with a handshake record (<c>0x16 0x03</c>, a ClientHello that
/// carries the server name), a plain one with the AMQP protocol header (<c>AMQP 0 0 9 1</c>).
/// </summary>
public sealed class RabbitMQTlsTests
{
    [Fact]
    public async Task Inline_single_host_with_ssl_speaks_tls()
    {
        await using var probe = new HandshakeProbe();
        var component = new RabbitMQComponent();

        var hello = await probe.CaptureAsync(component, InlineEndpoint(component, $"host=localhost&port={probe.Port}&ssl=true"));

        hello.IsTls.Should().BeTrue("ssl=true must never go out as plain AMQP; got {0}", hello);
    }

    [Fact]
    public async Task Inline_cluster_with_ssl_speaks_tls()
    {
        await using var probe = new HandshakeProbe();
        var component = new RabbitMQComponent();

        var hello = await probe.CaptureAsync(component, InlineEndpoint(component, $"host=localhost,127.0.0.1&port={probe.Port}&ssl=true"));

        hello.IsTls.Should().BeTrue("a host list must not drop TLS; got {0}", hello);
    }

    [Fact]
    public async Task Named_factory_with_ssl_speaks_tls()
    {
        await using var probe = new HandshakeProbe();
        var (component, _) = WithFactory("tls", new RabbitMQConnectionFactory { Host = "localhost", Port = probe.Port, Ssl = true, AutomaticRecovery = false });

        var hello = await probe.CaptureAsync(component, FactoryEndpoint(component, "tls"));

        hello.IsTls.Should().BeTrue("a named factory with Ssl=true must not connect in plain text; got {0}", hello);
    }

    [Fact]
    public async Task Named_factory_cluster_with_ssl_speaks_tls()
    {
        await using var probe = new HandshakeProbe();
        var (component, _) = WithFactory("tls", new RabbitMQConnectionFactory { Host = "localhost, 127.0.0.1", Port = probe.Port, Ssl = true, AutomaticRecovery = false });

        var hello = await probe.CaptureAsync(component, FactoryEndpoint(component, "tls"));

        hello.IsTls.Should().BeTrue("a named factory host list must not drop TLS; got {0}", hello);
    }

    [Fact]
    public async Task Server_name_defaults_to_the_host_being_connected()
    {
        await using var probe = new HandshakeProbe();
        var component = new RabbitMQComponent();

        var hello = await probe.CaptureAsync(component, InlineEndpoint(component, $"host=localhost&port={probe.Port}&ssl=true"));

        hello.IsTls.Should().BeTrue();
        hello.Contains("localhost").Should().BeTrue(
            "without sslServerName the certificate is checked against the host we connect to, so the ClientHello names it");
    }

    [Fact]
    public async Task Explicit_server_name_is_sent_to_every_host()
    {
        await using var probe = new HandshakeProbe();
        var (component, _) = WithFactory("tls", new RabbitMQConnectionFactory
        {
            Host = "localhost", Port = probe.Port, Ssl = true, SslServerName = "rabbit.example", AutomaticRecovery = false,
        });

        var hello = await probe.CaptureAsync(component, FactoryEndpoint(component, "tls"));

        hello.IsTls.Should().BeTrue();
        hello.Contains("rabbit.example").Should().BeTrue("the configured server name wins over the host name");
    }

    [Fact]
    public async Task Named_factory_cluster_fails_over_to_the_next_host()
    {
        // 127.0.0.2 is loopback too, but nothing listens on it: the connection is refused and the
        // client must move on to the next host of the factory, where the probe listens.
        await using var probe = new HandshakeProbe();
        var (component, _) = WithFactory("cluster", new RabbitMQConnectionFactory { Host = "127.0.0.2, 127.0.0.1", Port = probe.Port, AutomaticRecovery = false });

        var hello = await probe.CaptureAsync(component, FactoryEndpoint(component, "cluster"));

        hello.IsAmqpHeader.Should().BeTrue("the factory's own host list is the cluster, not the endpoint URI's default host; got {0}", hello);
    }

    [Fact]
    public void Factory_endpoints_carry_tls_with_their_own_server_name()
    {
        using var pfx = TestCertificates.ClientPfx(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var factory = new RabbitMQConnectionFactory
        {
            Host = "r1, r2", Port = 5671, Ssl = true, SslCertPath = pfx.Path, SslCertPassword = TestCertificates.Passphrase,
        };

        var endpoints = factory.GetEndpoints();

        // The certificate is loaded (and checked) by the connector and handed over as the certificate itself.
        endpoints.Should().HaveCount(2);
        endpoints.Should().OnlyContain(e => e.Ssl.Enabled && e.Ssl.Certs != null && e.Ssl.Certs.Count == 1
            && e.Ssl.Certs[0].Subject == "CN=redb-test-client");
        endpoints[0].Ssl.ServerName.Should().Be("r1");
        endpoints[1].Ssl.ServerName.Should().Be("r2");
    }

    [Fact]
    public void Factory_build_keeps_tls()
    {
        var built = new RabbitMQConnectionFactory { Host = "r1", Port = 5671, Ssl = true }.Build();

        built.Ssl.Enabled.Should().BeTrue();
        built.Ssl.ServerName.Should().Be("r1");
        built.Endpoint.Ssl.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Factory_without_ssl_stays_plain()
    {
        var factory = new RabbitMQConnectionFactory { Host = "r1, r2" };

        factory.GetEndpoints().Should().OnlyContain(e => !e.Ssl.Enabled);
        factory.Build().Ssl.Enabled.Should().BeFalse();
    }

    [Theory]
    [InlineData("sslServerName=a.example", "sslServerName=b.example")]
    [InlineData("sslCertPath=a.pfx", "sslCertPath=b.pfx")]
    public void Inline_endpoints_with_different_tls_identity_do_not_share_a_connection(string a, string b)
    {
        var keyA = RabbitMQComponent.ResolveConnectionKey(Options($"host=h&ssl=true&{a}"));
        var keyB = RabbitMQComponent.ResolveConnectionKey(Options($"host=h&ssl=true&{b}"));

        keyA.Should().NotBe(keyB, "a pooled connection is one TLS identity; another client certificate or server name is another connection");
    }

    private static RabbitMQEndpointOptions Options(string query)
    {
        var options = new RabbitMQEndpointOptions();
        options.BindFromUri(EndpointUriParser.Parse($"rabbitmq://q?{query}").RawParameters);
        return options;
    }

    private static RabbitMQEndpoint InlineEndpoint(RabbitMQComponent component, string query)
        => (RabbitMQEndpoint)component.CreateEndpoint(
            EndpointUriParser.Parse($"rabbitmq://tls-probe?{query}&automaticRecovery=false&connectionTimeout=10"));

    /// <summary>An endpoint on a named factory: the factory is the whole connection, the URI names nothing of it.</summary>
    private static RabbitMQEndpoint FactoryEndpoint(RabbitMQComponent component, string factoryName)
        => (RabbitMQEndpoint)component.CreateEndpoint(EndpointUriParser.Parse($"rabbitmq://tls-probe?connectionFactory={factoryName}"));

    private static (RabbitMQComponent Component, RouteContext Context) WithFactory(string name, RabbitMQConnectionFactory factory)
    {
        var component = new RabbitMQComponent();
        var context = new RouteContext();
        context.AddComponent(component);
        context.AddToRegistry(name, factory);
        return (component, context);
    }

    /// <summary>The first record the client sent.</summary>
    private sealed record FirstBytes(byte[] Data)
    {
        public bool IsTls => Data.Length >= 2 && Data[0] == 0x16 && Data[1] == 0x03;

        public bool IsAmqpHeader => Data.Length >= 4 && Encoding.ASCII.GetString(Data, 0, 4) == "AMQP";

        public bool Contains(string ascii) => Encoding.ASCII.GetString(Data).Contains(ascii, StringComparison.Ordinal);

        public override string ToString() => Data.Length == 0 ? "<nothing>" : Convert.ToHexString(Data.AsSpan(0, Math.Min(8, Data.Length)));
    }

    /// <summary>A loopback listener that accepts one connection and reads what the client says first.</summary>
    private sealed class HandshakeProbe : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public HandshakeProbe()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        public int Port { get; }

        public async Task<FirstBytes> CaptureAsync(RabbitMQComponent component, RabbitMQEndpoint endpoint)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var connecting = component.GetOrCreateConnectionAsync(endpoint, RabbitMQConnectionUse.Consume, cts.Token);

            FirstBytes first;
            using (var client = await _listener.AcceptTcpClientAsync(cts.Token))
            {
                first = await ReadFirstRecordAsync(client.GetStream(), cts.Token);
            }

            // The probe is no broker: the client fails once the socket is gone. Only the bytes matter.
            await FluentActions.Awaiting(() => connecting).Should().ThrowAsync<Exception>();
            return first;
        }

        private static async Task<FirstBytes> ReadFirstRecordAsync(NetworkStream stream, CancellationToken ct)
        {
            var header = await ReadAsync(stream, 5, ct);
            if (header.Length < 5 || header[0] != 0x16)
                return new FirstBytes(header);

            // TLS record: type(1) version(2) length(2), then the ClientHello with the SNI extension.
            var body = await ReadAsync(stream, (header[3] << 8) | header[4], ct);
            return new FirstBytes([.. header, .. body]);
        }

        private static async Task<byte[]> ReadAsync(NetworkStream stream, int count, CancellationToken ct)
        {
            var buffer = new byte[count];
            var read = 0;
            while (read < count)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
                if (n == 0) break;
                read += n;
            }
            return buffer[..read];
        }

        public ValueTask DisposeAsync()
        {
            _listener.Stop();
            _listener.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
