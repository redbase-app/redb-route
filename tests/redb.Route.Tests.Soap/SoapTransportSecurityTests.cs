using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;
using redb.Route.Soap;
using SoapDsl = redb.Route.Soap.Fluent.Soap;

namespace redb.Route.Tests.Soap;

/// <summary>
/// The consumer's transport security: TLS material and the client-certificate policy actually reach the
/// listener, and a configuration that cannot work is refused at start rather than at the first handshake.
/// </summary>
public class SoapTransportSecurityTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public void Ssl_Listener_Uri_Carries_The_Scheme_And_The_Certificate_Path()
    {
        var uri = SoapDsl.Listen("/sts").Host("127.0.0.1").Port(8443)
            .Ssl("/etc/certs/sts.pfx")
            .ClientCertificate(SoapClientCertificateMode.RequireCertificate, "AA11,BB22")
            .Build();

        // The scheme is what the component reads TLS from; a parameter alone would leave it plaintext.
        uri.Should().StartWith("soaps:/sts");
        uri.Should().Contain("sslCertPath=");
        uri.Should().Contain("clientCertificateMode=RequireCertificate");
        uri.Should().Contain("allowedClientThumbprints=AA11%2CBB22");
    }

    [Fact]
    public void The_Endpoint_Reads_Tls_Settings_From_The_Uri()
    {
        var component = new SoapComponent();
        var endpoint = (SoapEndpoint)component.CreateEndpoint(EndpointUriParser.Parse(
            SoapDsl.Listen("/sts").Host("127.0.0.1").Port(8443)
                .Ssl("/etc/certs/sts.pfx")
                .ClientCertificate(SoapClientCertificateMode.AllowCertificate, "AA11")
                .Build()));

        var options = endpoint.SoapOptions;
        options.UseTls.Should().BeTrue();
        options.SslCertPath.Should().Be("/etc/certs/sts.pfx");
        options.ClientCertificateMode.Should().Be(SoapClientCertificateMode.AllowCertificate);
        options.AllowedClientThumbprints.Should().Be("AA11");
    }
    /// <summary>
    /// Builds a consumer the way the context does, but without the context: <c>RouteContext.Start</c>
    /// logs a consumer that fails to start and carries on with the rest, which is right for a running
    /// process and useless for asserting that a refusal happened.
    /// </summary>
    private static (RouteContext Context, IConsumer Consumer) BuildConsumer(
        string uri, Action<RouteContext>? configure = null)
    {
        var ctx = new RouteContext();
        var component = new SoapComponent();
        ctx.AddComponent(component);
        configure?.Invoke(ctx);

        var endpoint = component.CreateEndpoint(EndpointUriParser.Parse(uri));
        return (ctx, endpoint.CreateConsumer(new DelegateProcessor(e => e.In.Body = "<ok/>")));
    }

    /// <summary>
    /// Without this the listener starts on HTTPS with no certificate, and Kestrel falls back to the
    /// development certificate: present on a developer's box, absent in production. The failure then
    /// surfaces far from its cause, which is the worst place for a TLS misconfiguration to surface.
    /// </summary>
    [Fact]
    public async Task Tls_Without_A_Certificate_Is_Refused_At_Start()
    {
        var (ctx, consumer) = BuildConsumer(
            SoapDsl.Listen("/sts").Host("127.0.0.1").Port(FreePort()).Ssl());
        await using var _ = ctx;

        var start = async () => await consumer.Start();

        (await start.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("no certificate");
    }

    /// <summary>
    /// A client certificate is presented during the TLS handshake. Asking for one on a plaintext listener
    /// is a policy that can never be enforced, and silently accepting it would leave an operator believing
    /// the endpoint is pinned to their partners when it is open to anyone.
    /// </summary>
    [Fact]
    public async Task Client_Certificates_Without_Tls_Are_Refused_At_Start()
    {
        var (ctx, consumer) = BuildConsumer(
            SoapDsl.Listen("/sts").Host("127.0.0.1").Port(FreePort())
                .ClientCertificate(SoapClientCertificateMode.RequireCertificate));
        await using var _ = ctx;

        var start = async () => await consumer.Start();

        (await start.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("without TLS");
    }

    /// <summary>
    /// The certificate password belongs on the registered factory, never in the URI: the URI is the route
    /// key and is handled as an ordinary string by logging, telemetry and the dashboard.
    /// </summary>
    [Fact]
    public async Task The_Factory_Supplies_Tls_Material_The_Uri_Does_Not_Carry()
    {
        var uri = SoapDsl.Listen("/sts").Host("127.0.0.1").Port(FreePort())
            .ConnectionFactory("sts-tls").Build();

        uri.Should().NotContain("sslCertPath");

        var (ctx, consumer) = BuildConsumer(uri, c => c.AddToRegistry("sts-tls",
            new SoapConnectionFactory
            {
                Ssl = true,
                SslCertPath = "/nonexistent/sts.pfx",
                SslCertPassword = "s3cret",
            }));
        await using var _ = ctx;

        var start = async () => await consumer.Start();

        // TLS came from the factory, so the certificate check passes and the failure is Kestrel's own:
        // the file is not there. Had the factory been ignored, the listener would have come up plaintext
        // and this would not have thrown at all.
        var thrown = (await start.Should().ThrowAsync<Exception>()).Which.ToString();
        thrown.Should().NotContain("no certificate");
        thrown.Should().NotContain("s3cret");
    }

    /// <summary>
    /// A route behind a SOAP endpoint could not learn who was calling: the consumer surfaced nothing
    /// from the connection at all. Every IP-keyed protection therefore saw nothing and silently did
    /// nothing, which reads as «no abuse» rather than as «not wired».
    /// </summary>
    [Fact]
    public async Task The_Callers_Address_Reaches_The_Route()
    {
        var port = FreePort();
        object? remote = null, compat = null;

        await using var ctx = new RouteContext();
        ctx.AddComponent(new SoapComponent());
        ctx.AddRoutes(r =>
        {
            r.From(SoapDsl.Listen("/svc").Host("127.0.0.1").Port(port).HttpCompatHeaders())
                .Process(e =>
                {
                    e.In.Headers.TryGetValue(SoapHeaders.RemoteAddress, out remote);
                    e.In.Headers.TryGetValue("redbHttp.RemoteAddress", out compat);
                    e.In.Body = "<Reply xmlns=\"urn:test\"><status>ok</status></Reply>";
                });
            r.From("direct://call").To(SoapDsl.Call($"http://127.0.0.1:{port}/svc").Operation("Ping"));
        });

        await ctx.Start();
        var producer = ctx.GetEndpoint("direct://call").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("<Ping xmlns=\"urn:test\"/>")));

        remote.Should().Be("127.0.0.1");

        // The HTTP-namespace bridge is what lets processors written for the HTTP transport work here
        // unchanged. It is opt-in, and this route asked for it.
        compat.Should().Be("127.0.0.1");
    }

    /// <summary>
    /// The bridge stays off unless a route asks: writing into another transport's namespace is a
    /// deliberate act, and the connector's own headers already carry the same facts.
    /// </summary>
    [Fact]
    public async Task The_Http_Namespace_Bridge_Is_Off_By_Default()
    {
        var port = FreePort();
        var sawCompat = true;
        object? remote = null;

        await using var ctx = new RouteContext();
        ctx.AddComponent(new SoapComponent());
        ctx.AddRoutes(r =>
        {
            r.From(SoapDsl.Listen("/svc").Host("127.0.0.1").Port(port))
                .Process(e =>
                {
                    sawCompat = e.In.Headers.ContainsKey("redbHttp.RemoteAddress");
                    e.In.Headers.TryGetValue(SoapHeaders.RemoteAddress, out remote);
                    e.In.Body = "<Reply xmlns=\"urn:test\"><status>ok</status></Reply>";
                });
            r.From("direct://call").To(SoapDsl.Call($"http://127.0.0.1:{port}/svc").Operation("Ping"));
        });

        await ctx.Start();
        var producer = ctx.GetEndpoint("direct://call").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("<Ping xmlns=\"urn:test\"/>")));

        sawCompat.Should().BeFalse();

        // The native header is unconditional: a route always gets to know who called.
        remote.Should().Be("127.0.0.1");
    }
}
