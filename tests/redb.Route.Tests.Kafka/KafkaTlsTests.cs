using Confluent.Kafka;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Kafka;

namespace redb.Route.Tests.Kafka;

/// <summary>
/// Encrypted and authenticated connections against the live 3-node cluster: its SECURE listeners (SASL_SSL,
/// SCRAM-SHA-512, user redb) and MTLS listeners (SSL, client certificate required), from C:\Work\yaml\kafka
/// (gen-tls.sh writes the test PKI; REDB_KAFKA_TLS_DIR points elsewhere). The broker certificate names kafka-k1..3
/// only, so reaching a broker as localhost fails hostname verification.
/// </summary>
[Trait("Category", "Integration")]
public sealed class KafkaTlsTests
{
    private const string SecureServers = "kafka-k1:29192,kafka-k2:29194,kafka-k3:29196";
    private const string MtlsServers = "kafka-k1:29292,kafka-k2:29294,kafka-k3:29296";
    private const string Scram = "securityProtocol=SaslSsl&saslMechanism=ScramSha512&saslUsername=redb&saslPassword=redb-secret";

    private static readonly string TlsDir =
        (Environment.GetEnvironmentVariable("REDB_KAFKA_TLS_DIR") ?? "C:/Work/yaml/kafka/secrets").Replace('\\', '/');

    private static string Pki(string file) => $"{TlsDir}/{file}";

    /// <summary>A failed handshake is a produce that never completes: bound it instead of librdkafka's 5 minutes.</summary>
    private const string FailFast = "messageTimeoutMs=8000";

    private static KafkaEndpoint Endpoint(string topic, string brokers, string parameters)
    {
        var uri = EndpointUriParser.Parse($"kafka://{topic}?brokers={brokers}&{parameters}");
        return (KafkaEndpoint)new KafkaComponent().CreateEndpoint(uri);
    }

    private static async Task Send(string topic, string brokers, string parameters, string value)
    {
        var producer = (KafkaProducer)Endpoint(topic, brokers, parameters).CreateProducer();
        await producer.Start();
        try
        {
            await producer.Process(new Exchange(new Message(value)));
        }
        finally
        {
            await producer.Stop();
        }
    }

    /// <summary>Reads the first record of <paramref name="topic"/> through a connector consumer on the same listener.</summary>
    private static async Task<string?> Receive(string topic, string brokers, string parameters)
    {
        var first = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                first.TrySetResult(System.Text.Encoding.UTF8.GetString((byte[])call.Arg<IExchange>().In.Body!));
                return Task.CompletedTask;
            });

        var consumer = (KafkaConsumer)Endpoint(topic, brokers,
                $"{parameters}&groupId=tls-{Guid.NewGuid():N}&autoOffsetReset=earliest")
            .CreateConsumer(processor);
        await consumer.Start();
        try
        {
            return await first.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            await consumer.Stop();
        }
    }

    private static string Topic(string name) => $"tls-{name}-{Guid.NewGuid():N}";

    [Fact]
    public async Task SaslSsl_Scram_with_the_clusters_CA_sends_and_receives()
    {
        var topic = Topic("scram");
        var parameters = $"{Scram}&sslCaLocation={Pki("ca.pem")}";

        await Send(topic, SecureServers, parameters, "over-tls");

        (await Receive(topic, SecureServers, parameters)).Should().Be("over-tls");
    }

    [Fact]
    public async Task A_broker_certificate_from_another_CA_is_refused()
    {
        var send = () => Send(Topic("rogue"), SecureServers, $"{Scram}&sslCaLocation={Pki("rogue-ca.pem")}&{FailFast}", "x");

        await send.Should().ThrowAsync<KafkaException>();
    }

    [Fact]
    public async Task A_wrong_scram_password_is_refused()
    {
        var parameters = $"securityProtocol=SaslSsl&saslMechanism=ScramSha512&saslUsername=redb&saslPassword=wrong" +
                         $"&sslCaLocation={Pki("ca.pem")}&{FailFast}";

        var send = () => Send(Topic("badpass"), SecureServers, parameters, "x");

        await send.Should().ThrowAsync<KafkaException>();
    }

    [Fact]
    public async Task A_broker_reached_by_a_name_its_certificate_lacks_is_refused()
    {
        // The certificate names kafka-k1..3; localhost is the same broker under a name it does not carry.
        var send = () => Send(Topic("hostname"), "localhost:29192", $"{Scram}&sslCaLocation={Pki("ca.pem")}&{FailFast}", "x");

        await send.Should().ThrowAsync<KafkaException>();
    }

    [Fact]
    public async Task Hostname_verification_none_lets_the_same_connection_through()
    {
        // Review of the TLS options: "none" used to mean https, like any value but the empty string.
        var topic = Topic("hostname-off");

        await Send(topic, "localhost:29192",
            $"{Scram}&sslCaLocation={Pki("ca.pem")}&sslEndpointIdentificationAlgorithm=none&{FailFast}", "no-check");

        (await Receive(topic, SecureServers, $"{Scram}&sslCaLocation={Pki("ca.pem")}")).Should().Be("no-check");
    }

    [Fact]
    public async Task Mutual_tls_with_the_client_certificate_sends_and_receives()
    {
        var topic = Topic("mtls");
        var parameters = $"securityProtocol=Ssl&sslCaLocation={Pki("ca.pem")}" +
                         $"&sslCertificateLocation={Pki("client.pem")}&sslKeyLocation={Pki("client.key")}";

        await Send(topic, MtlsServers, parameters, "mutual");

        (await Receive(topic, MtlsServers, parameters)).Should().Be("mutual");
    }

    [Fact]
    public async Task Mutual_tls_without_a_client_certificate_is_refused()
    {
        var send = () => Send(Topic("mtls-nocert"), MtlsServers, $"securityProtocol=Ssl&sslCaLocation={Pki("ca.pem")}&{FailFast}", "x");

        await send.Should().ThrowAsync<KafkaException>();
    }

    [Fact]
    public async Task A_named_connection_factory_carries_tls_and_scram()
    {
        var topic = Topic("factory");
        await using var context = new RouteContext();
        context.AddComponent(new KafkaComponent());
        context.AddToRegistry("secure", new KafkaConnectionFactory
        {
            Brokers = SecureServers,
            SecurityProtocol = "SaslSsl",
            SaslMechanism = "ScramSha512",
            SaslUsername = "redb",
            SaslPassword = "redb-secret",
            SslCaLocation = Pki("ca.pem"),
        });

        var producer = (KafkaProducer)context.GetEndpoint($"kafka://{topic}?connectionFactory=secure").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("via-factory")));
        await producer.Stop();

        (await Receive(topic, SecureServers, $"{Scram}&sslCaLocation={Pki("ca.pem")}")).Should().Be("via-factory");
    }
}
