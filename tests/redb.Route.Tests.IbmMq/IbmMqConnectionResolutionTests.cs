using IBM.WMQ;
using redb.Route.Core;
using redb.Route.IbmMq;

namespace redb.Route.Tests.IbmMq;

/// <summary>
/// Row 54: <c>connectionFactory=name</c> connects through the <see cref="IbmMqConnectionFactory"/> registered under that
/// name, which takes priority over the inline options; a name nothing is registered under fails when the endpoint is
/// created. Before, the name only split the connection pool and the connection came from the inline options or their
/// defaults. Without <c>connectionFactory</c> the inline options describe the connection exactly as before. No broker.
/// </summary>
public sealed class IbmMqConnectionResolutionTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();
    private readonly IbmMqComponent _component = new();

    public IbmMqConnectionResolutionTests() => _context.AddComponent(_component);

    public async ValueTask DisposeAsync() => await _context.DisposeAsync();

    private IbmMqEndpoint Endpoint(string query)
        => (IbmMqEndpoint)_component.CreateEndpoint(EndpointUriParser.Parse($"wmq:DEV.QUEUE.1?{query}"));

    [Fact]
    public void ARegisteredFactory_IsWhatTheConnectionIsBuiltFrom_OverTheInlineOptions()
    {
        var factory = new IbmMqConnectionFactory
        {
            Host = "mq.prod.internal", Port = 1515, Channel = "PROD.APP.SVRCONN", QueueManager = "QMPROD",
            User = "svc-orders", Password = "s3cret", ClientName = "orders",
        };
        _context.AddToRegistry("prod", factory);

        var connection = Endpoint("connectionFactory=prod&host=inline-host&channel=INLINE.SVRCONN").Connection;

        connection.Should().BeSameAs(factory);
        var properties = connection.BuildConnectionProperties();
        properties[MQC.HOST_NAME_PROPERTY].Should().Be("mq.prod.internal");
        properties[MQC.PORT_PROPERTY].Should().Be(1515);
        properties[MQC.CHANNEL_PROPERTY].Should().Be("PROD.APP.SVRCONN");
        properties[MQC.USER_ID_PROPERTY].Should().Be("svc-orders");
        properties[MQC.PASSWORD_PROPERTY].Should().Be("s3cret");
        connection.QueueManager.Should().Be("QMPROD");
    }

    [Fact]
    public void ANameNothingIsRegisteredUnder_FailsWhenTheEndpointIsCreated()
    {
        var act = () => Endpoint("connectionFactory=nope");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("'nope' is not registered in the context registry (expected IbmMqConnectionFactory)*");
    }

    [Fact]
    public void AHashName_IsTheSameRegistryKey()
    {
        var factory = new IbmMqConnectionFactory { Host = "mq.hash" };
        _context.AddToRegistry("prod", factory);

        Endpoint("connectionFactory=%23prod").Connection.Should().BeSameAs(factory);
    }

    [Fact]
    public void WithoutAFactory_TheInlineOptionsDescribeTheConnectionAsBefore()
    {
        var connection = Endpoint("host=mq.inline&port=1416&channel=IN.SVRCONN&queueManager=QMI&user=u1&password=p1" +
                                  "&clientId=app1&sslCipherSpec=TLS_AES_128_GCM_SHA256&sslKeyResetCount=4096" +
                                  "&sslCertLabel=lbl&sslPeerName=CN%3Dmq&ccsid=819").Connection;

        connection.QueueManager.Should().Be("QMI");
        // The table the inline builder of the component produced before the change, entry for entry.
        connection.BuildConnectionProperties().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => e.Value)
            .Should().BeEquivalentTo(new Dictionary<string, object?>
            {
                [MQC.TRANSPORT_PROPERTY] = MQC.TRANSPORT_MQSERIES_MANAGED,
                [MQC.HOST_NAME_PROPERTY] = "mq.inline",
                [MQC.PORT_PROPERTY] = 1416,
                [MQC.CHANNEL_PROPERTY] = "IN.SVRCONN",
                [MQC.CCSID_PROPERTY] = 819,
                [MQC.USER_ID_PROPERTY] = "u1",
                [MQC.PASSWORD_PROPERTY] = "p1",
                [MQC.APPNAME_PROPERTY] = "app1",
                [MQC.SSL_CIPHER_SPEC_PROPERTY] = "TLS_AES_128_GCM_SHA256",
                [MQC.SSL_RESET_COUNT_PROPERTY] = 4096,
                [MQC.SSL_CERT_STORE_PROPERTY] = "lbl",
                [MQC.SSL_PEER_NAME_PROPERTY] = "CN=mq",
            });
    }

    [Fact]
    public void TheOption_IsMarkedAsARegistryReferenceToTheFactory()
    {
        typeof(IbmMqEndpointOptions).GetProperty(nameof(IbmMqEndpointOptions.ConnectionFactory))!
            .GetCustomAttributes(typeof(RegistryReferenceAttribute), inherit: true)
            .Cast<RegistryReferenceAttribute>().Single().Expected.Should().Be(typeof(IbmMqConnectionFactory));
    }
}
