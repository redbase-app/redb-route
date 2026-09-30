using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using redb.Route.As4;
using redb.Route.As4.Messaging;
using redb.Route.As4.Mime;
using redb.Route.As4.Security;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Processors;
using redb.Route.Xml;

namespace redb.Route.Tests.As4;

/// <summary>
/// Ф8: the AS4 node is written in Route-XML as its README shows — a <c>&lt;bean&gt;</c> whose <c>Partners</c> is a
/// <c>&lt;list&gt;</c> of <c>&lt;ref&gt;</c> partners, certificates loaded by nested beans — and an <c>as4:</c> route from
/// XML receives a message and answers with a receipt. The Route-XML grammar is the one <c>XmlBeanListAndRefTests</c> covers.
/// </summary>
public sealed class As4RouteXmlTests : IDisposable
{
    private const string Password = "xml-test";
    private static readonly X509Certificate2 NodeA = As4TestMessages.KeyPair("CN=a.as4.test");
    private static readonly X509Certificate2 NodeB = As4TestMessages.KeyPair("CN=b.as4.test");
    private readonly string _dir = Directory.CreateTempSubdirectory("redb-as4-xml-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task NodeAndRouteFromXml_ReceiveAMessage_AndAnswerWithAReceipt()
    {
        // The PFX through the X509Certificate2 constructor, the overload named by <constructorArg type=…>: it runs on net8,
        // where X509CertificateLoader (the README's loader) does not exist yet.
        File.WriteAllBytes(Path.Combine(_dir, "b.pfx"), NodeB.Export(X509ContentType.Pfx, Password));
        File.WriteAllBytes(Path.Combine(_dir, "a.cer"), NodeA.Export(X509ContentType.Cert));
        var port = global::redb.Route.Tests.Shared.TestPorts.Next();
        const string certificate = "System.Security.Cryptography.X509Certificates.X509Certificate2, System.Security.Cryptography";

        await using var context = new RouteContext();
        context.AddComponent(new As4Component());
        var delivered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.AddRoutes(r => r.From("direct://as4-inbound").Process(e => delivered.TrySetResult(e.In.GetHeader<string>(As4Headers.Partner)!)));

        context.AddXmlContextFromContent($"""
            <context xmlns="urn:redb:route:1.0">
              <bean name="dedup" type="redb.Route.Processors.InMemoryIdempotentRepository, redb.Route"/>
              <bean name="a" type="redb.Route.As4.As4Partner, redb.Route.As4">
                <property key="Name" value="a"/>
                <property key="PartyId" value="urn:redb:a"/>
                <property key="Service" value="urn:example"/>
                <property key="Action" value="Submit"/>
                <property key="PartnerSigningCertificates">
                  <list><bean type="{certificate}"><constructorArg type="System.String" value="{_dir}/a.cer"/></bean></list>
                </property>
                <property key="PartnerEncryptionCertificate">
                  <bean type="{certificate}"><constructorArg type="System.String" value="{_dir}/a.cer"/></bean>
                </property>
              </bean>
              <bean name="b-key" type="{certificate}">
                <constructorArg type="System.String" value="{_dir}/b.pfx"/>
                <constructorArg type="System.String" value="{Password}"/>
              </bean>
              <bean name="b" type="redb.Route.As4.As4ConnectionFactory, redb.Route.As4">
                <property key="OurPartyId" value="urn:redb:b"/>
                <property key="ExternalHostName" value="b.example"/>
                <property key="SigningCertificate" ref="b-key"/>
                <property key="DecryptionCertificates"><list><ref bean="b-key"/></list></property>
                <property key="Partners"><list><ref bean="a"/></list></property>
              </bean>
            </context>
            """);
        context.AddXmlRoutesFromContent($"""
            <routes xmlns="urn:redb:route:1.0">
              <route id="as4-in">
                <from uri="as4:/as4/in?host=127.0.0.1&amp;port={port}&amp;connectionFactory=b&amp;idempotentRepository=dedup"/>
                <to uri="direct://as4-inbound"/>
              </route>
            </routes>
            """);
        await context.Start();

        var (errorCode, receipt) = await Post(port);

        errorCode.Should().BeNull();
        receipt.Should().BeTrue();
        (await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("a");
    }

    private async Task<(string? ErrorCode, bool Receipt)> Post(int port)
    {
        const string contentId = "p1@a.example";
        using var message = SwaMessage.Create(As4TestMessages.UserMessage(Guid.NewGuid().ToString("N") + "@a.example", contentId,
            from: "urn:redb:a", to: "urn:redb:b", service: "urn:example", action: "Submit"));
        message.AddPart(contentId, "application/gzip", As4TestMessages.Gzip(Encoding.UTF8.GetBytes("<invoice/>")));
        As4SecurityEngine.Sign(message, NodeA, As4KeyReference.BinarySecurityToken, null, DateTimeOffset.UtcNow);
        As4SecurityEngine.Encrypt(message, As4TestMessages.PublicOnly(NodeB), As4KeyReference.BinarySecurityToken);
        var (contentType, body) = message.Write();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        using var response = await http.PostAsync($"http://127.0.0.1:{port}/as4/in", content);
        var answer = SwaMessage.Read(response.Content.Headers.ContentType!.ToString(), await response.Content.ReadAsByteArrayAsync(), 1024 * 1024);
        var signals = MessagingReader.Read(answer.Envelope).SignalMessages;
        return (signals.SelectMany(s => s.Errors).FirstOrDefault()?.ErrorCode, signals.Any(s => s.IsReceipt));
    }
}
