using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using redb.Route.Core;
using redb.Route.Http;
using redb.Route.Http.Rest;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml;

/// <summary>
/// Client request validation of <c>&lt;rest&gt;</c> in Route-XML: <c>clientRequestValidation</c> and
/// <c>errorHandler</c> on the declaration, <c>clientRequestValidation</c> and <c>&lt;param&gt;</c>
/// children on a verb — parsed into the same declarations as the fluent DSL, printed as the same
/// C#, refused with a positioned error when malformed.
/// </summary>
public class RestXmlValidationTests
{
    private static readonly XmlRouteLoaderOptions Options = new()
    {
        Extensions = [new RestXmlContribution()],
    };

    [Fact]
    public async Task Params_and_the_option_reach_the_running_route_and_the_openapi_document()
    {
        var port = FreePort();
        await using var servers = new SharedHttpServerManager();
        await using var context = new RouteContext();
        context.AddComponent(new HttpComponent { ServerManager = servers });
        context.AddXmlRoutesFromContent($"""
            <routes xmlns="urn:redb:route:1.0">
              <rest path="/api" host="127.0.0.1" port="{port}" clientRequestValidation="true">
                <get path="/items/{"{id}"}" produces="application/json">
                  <param name="id" type="path" dataType="integer"/>
                  <param name="limit" required="true" dataType="integer" description="Page size"/>
                  <setBody value="ok"/>
                </get>
                <get path="/free" produces="application/json" clientRequestValidation="false">
                  <setBody value="free"/>
                </get>
              </rest>
            </routes>
            """, options: Options);
        await context.Start();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        (await client.GetAsync("/api/items/7?limit=5")).StatusCode.Should().Be(HttpStatusCode.OK);
        var missing = await client.GetAsync("/api/items/7");
        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await missing.Content.ReadAsStringAsync()).Should().Contain("limit");
        (await client.GetAsync("/api/items/x?limit=5")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var wrongAccept = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, "/api/free");
        wrongAccept.Headers.TryAddWithoutValidation("Accept", "application/xml");
        (await client.SendAsync(wrongAccept)).StatusCode.Should().Be(HttpStatusCode.OK, "the verb switched validation off");

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/openapi.json"));
        var parameters = doc.RootElement.GetProperty("paths").GetProperty("/api/items/{id}").GetProperty("get")
            .GetProperty("parameters").EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!);
        parameters["id"].GetProperty("schema").GetProperty("type").GetString().Should().Be("integer");
        parameters["limit"].GetProperty("in").GetString().Should().Be("query");
        parameters["limit"].GetProperty("description").GetString().Should().Be("Page size");
    }

    [Fact]
    public void Params_print_as_the_fluent_calls()
    {
        var code = XmlCodeGenerator.Generate(XDocument.Parse("""
            <routes xmlns="urn:redb:route:1.0">
              <rest path="/api" port="5099" clientRequestValidation="true" errorHandler="#errors">
                <get path="/{id}" clientRequestValidation="false" to="direct://x">
                  <param name="id" type="path" dataType="integer"/>
                  <param name="X-Tenant" type="header" required="true" description="Tenant"/>
                </get>
                <post>
                  <param name="dryRun" dataType="boolean"/>
                  <removeBody/>
                </post>
              </rest>
            </routes>
            """, LoadOptions.SetLineInfo), "RestRoutes", "Demo", options: Options);

        code.Should().Contain("options.ClientRequestValidation = true;")
            .And.Contain("options.ErrorHandler = \"#errors\";")
            .And.Contain(".Get(\"/{id}\").ClientRequestValidation(false).Param(\"id\", RestParamType.Path, dataType: RestParamDataType.Integer)"
                + ".Param(\"X-Tenant\", RestParamType.Header, required: true, description: \"Tenant\").To(\"direct://x\");")
            .And.Contain(".Post(\"\").Param(\"dryRun\", dataType: RestParamDataType.Boolean).Route()")
            .And.Contain("RemoveBody()");
    }

    [Theory]
    [InlineData("""<param name="limit" dataType="int"/>""", "*dataType='int'*string, integer, number, boolean*")]
    [InlineData("""<param name="limit" type="cookie"/>""", "*type='cookie'*path, query, header*")]
    [InlineData("""<param name="code" type="path"/>""", "*code*not a segment*")]
    [InlineData("""<param name="id" type="path" required="false"/>""", "*cannot be optional*")]
    [InlineData("""<param name="limit"/><param name="limit"/>""", "*limit*declared twice*")]
    public async Task A_malformed_param_is_a_positioned_schema_error(string param, string message)
    {
        await using var context = new RouteContext();

        var act = () => context.AddXmlRoutesFromContent($"""
            <routes xmlns="urn:redb:route:1.0">
              <rest path="/api" openApi="false">
                <get path="/{"{id}"}" to="direct://x">{param}</get>
              </rest>
            </routes>
            """, options: Options);

        act.Should().Throw<XmlRouteException>().WithMessage(message);
    }

    [Fact]
    public async Task Inbound_basic_auth_on_rest_protects_its_verbs()
    {
        var port = FreePort();
        await using var servers = new SharedHttpServerManager();
        await using var context = new RouteContext();
        context.AddComponent(new HttpComponent { ServerManager = servers });
        context.AddXmlRoutesFromContent($"""
            <routes xmlns="urn:redb:route:1.0">
              <rest path="/api" host="127.0.0.1" port="{port}" openApi="false"
                    inboundAuth="basic" inboundUsername="admin" inboundPassword="s3cret" inboundRealm="orders">
                <get path="/items"><setBody value="ok"/></get>
              </rest>
            </routes>
            """, options: Options);
        await context.Start();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var refused = await client.GetAsync("/api/items");
        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        refused.Headers.WwwAuthenticate.ToString().Should().Contain("realm=\"orders\"");

        using var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, "/api/items");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("admin:s3cret")));
        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void Inbound_auth_prints_as_the_fluent_options()
    {
        var code = XmlCodeGenerator.Generate(XDocument.Parse("""
            <routes xmlns="urn:redb:route:1.0">
              <rest path="/api" port="5099" inboundAuth="bearer" tokenValidator="#tokens" inboundRealm="orders">
                <get path="/x" to="direct://x"/>
              </rest>
            </routes>
            """, LoadOptions.SetLineInfo), "RestRoutes", "Demo", options: Options);

        code.Should().Contain("using redb.Route.Http;").And.Contain("using redb.Route.Http.Rest;")
            .And.Contain("options.InboundAuth = HttpAuthScheme.Bearer;")
            .And.Contain("options.TokenValidator = \"#tokens\";")
            .And.Contain("options.InboundRealm = \"orders\";");
    }

    private static int FreePort() => global::redb.Route.Tests.Shared.TestPorts.Next();
}
