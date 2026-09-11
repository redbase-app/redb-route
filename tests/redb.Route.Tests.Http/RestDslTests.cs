using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;
using redb.Route.Http.Rest;

namespace redb.Route.Tests.Http;

/// <summary>REST DSL on top of the HTTP consumer: real Kestrel, real HttpClient.</summary>
[Collection("HttpServer")]
public class RestDslTests : IAsyncLifetime
{
    public sealed class Order
    {
        public int Id { get; set; }
        public string Customer { get; set; } = "";
    }

    public static class V1 { public sealed class Order { public int Id { get; set; } } }

    public static class V2 { public sealed class Order { public string Sku { get; set; } = ""; } }

    private sealed class OrdersApi(int port) : RouteBuilder
    {
        protected override void Configure()
        {
            this.Rest("/api/orders", o => { o.Host = "127.0.0.1"; o.Port = port; o.BindingMode = RestBindingMode.Json; })
                .Get("/{id}").Produces("application/json").OutType<Order>().Description("Get an order").To("direct://get-order")
                .Post().Consumes("application/json").Type<Order>().Id("createOrder").To("direct://create-order")
                .Put("/{id}/status").Produces("text/plain").To("direct://set-status")
                .Delete("/{id}").Route().Process(e => e.In.Body = null);
            var rest = this.Rest("/api/misc", o => { o.Host = "127.0.0.1"; o.Port = port; });
            rest.Get("/boom").Route().ThrowException<InvalidOperationException>("boom");
            rest.Get("/health").Route().SetBody("ok");

            // Code review 2026-09-01: two types with one short name, two inline routes whose ids sanitize
            // alike, and a handler that reports whether a client could smuggle a query parameter in.
            var v = this.Rest("/api/v", o => { o.Host = "127.0.0.1"; o.Port = port; o.BindingMode = RestBindingMode.Json; });
            v.Post("/one").Consumes("application/json").Type<V1.Order>().To("direct://v-one");
            v.Post("/two").Consumes("application/json").Type<V2.Order>().To("direct://v-two");
            v.Get("/{id}").Route().SetBody("by-template");
            v.Get("/id").Route().SetBody("fixed");
            v.Get("/whoami").Route().Process(e => e.In.Body = e.In.Headers.ContainsKey("query.role") ? "spoofed" : "clean");
            From("direct://v-one").SetBody("one");
            From("direct://v-two").SetBody("two");

            From("direct://get-order").Process(e =>
            {
                e.In.Headers.TryGetValue("query.customer", out var customer);
                e.In.Body = new Order { Id = int.Parse((string)e.In.Headers["id"]!), Customer = customer as string ?? "n/a" };
            });
            From("direct://create-order").Process(e =>
            {
                var order = (Order)e.In.Body!;
                e.In.Headers[HttpHeaders.ResponseCode] = 201;
                e.In.Body = new Order { Id = order.Id + 1000, Customer = order.Customer };
            });
            From("direct://set-status").Process(e => e.In.Body = $"status of {e.In.Headers["id"]}");
        }
    }

    private int _port;
    private HttpClient _client = null!;
    private RouteContext _ctx = null!;
    private SharedHttpServerManager _serverManager = null!;

    public async Task InitializeAsync()
    {
        _port = GetFreePort();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
        _serverManager = new SharedHttpServerManager();
        _ctx = new RouteContext();
        _ctx.AddComponent(new HttpComponent { ServerManager = _serverManager });
        _ctx.AddRoutes(new OrdersApi(_port));
        await _ctx.Start();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _ctx.DisposeAsync();
        await _serverManager.DisposeAsync();
    }

    [Fact]
    public async Task Get_PathParameterAndQuery_BecomeHeaders_ResponseIsJson()
    {
        var response = await _client.GetAsync("/api/orders/7?customer=acme");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var order = await response.Content.ReadFromJsonAsync<Order>();
        order!.Id.Should().Be(7);
        order.Customer.Should().Be("acme");
    }

    [Fact]
    public async Task Post_JsonBinding_UnmarshalsRequest_MarshalsResponse_StatusFromHeader()
    {
        var response = await _client.PostAsync("/api/orders",
            new StringContent("""{"id":1,"customer":"bob"}""", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var order = await response.Content.ReadFromJsonAsync<Order>();
        order!.Id.Should().Be(1001);
        order.Customer.Should().Be("bob");
    }

    [Fact]
    public async Task Post_WrongContentType_Is415()
    {
        var response = await _client.PostAsync("/api/orders", new StringContent("<order/>", Encoding.UTF8, "application/xml"));

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        (await response.Content.ReadAsStringAsync()).Should().Contain("application/json");
    }

    [Fact]
    public async Task Put_PlainTextResponse()
    {
        var response = await _client.PutAsync("/api/orders/7/status", new StringContent("shipped"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("status of 7");
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
    }

    [Fact]
    public async Task Delete_InlineRoute_NoBody_Is204()
    {
        var response = await _client.DeleteAsync("/api/orders/7");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task UndeclaredMethod_Is405_UnknownPath_Is404()
    {
        (await _client.PostAsync("/api/orders/7/status", new StringContent("x"))).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        (await _client.GetAsync("/api/nothing")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UnhandledException_Is500()
    {
        (await _client.GetAsync("/api/misc/boom")).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task TwoDeclarations_ShareOnePort()
    {
        (await _client.GetStringAsync("/api/misc/health")).Should().Be("ok");
        (await _client.GetAsync("/api/orders/1")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task OpenApi_DescribesTheDeclaredOperations()
    {
        var response = await _client.GetAsync("/api/orders/openapi.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        root.GetProperty("openapi").GetString().Should().Be("3.0.3");
        root.GetProperty("info").GetProperty("title").GetString().Should().Be("redb.Route API");

        var paths = root.GetProperty("paths");
        var byId = paths.GetProperty("/api/orders/{id}");
        byId.GetProperty("get").GetProperty("summary").GetString().Should().Be("Get an order");
        byId.GetProperty("get").GetProperty("parameters")[0].GetProperty("name").GetString().Should().Be("id");
        byId.GetProperty("get").GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString().Should().Be("#/components/schemas/Order");
        byId.TryGetProperty("delete", out _).Should().BeTrue();
        paths.GetProperty("/api/orders/{id}/status").TryGetProperty("put", out _).Should().BeTrue();

        var post = paths.GetProperty("/api/orders").GetProperty("post");
        post.GetProperty("operationId").GetString().Should().Be("createOrder");
        post.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString()
            .Should().Be("#/components/schemas/Order");

        var order = root.GetProperty("components").GetProperty("schemas").GetProperty("Order").GetProperty("properties");
        order.GetProperty("id").GetProperty("type").GetString().Should().Be("integer");
        order.GetProperty("customer").GetProperty("type").GetString().Should().Be("string");

        paths.TryGetProperty("/api/misc/health", out _).Should().BeFalse("the second declaration has its own document");
        (await _client.GetAsync("/api/misc/openapi.json")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // Code review 2026-09-01, fourth batch.

    [Fact]
    public async Task OpenApi_TwoTypesWithOneShortName_GetTwoSchemas()
    {
        using var doc = JsonDocument.Parse(await _client.GetStringAsync("/api/v/openapi.json"));

        var names = doc.RootElement.GetProperty("components").GetProperty("schemas").EnumerateObject().Select(p => p.Name).ToList();
        names.Should().Contain("Order");
        names.Should().ContainSingle(n => n.Contains("V2") && n.EndsWith("Order"), "V2.Order must not resolve to V1.Order's schema");
    }

    [Fact]
    public async Task InlineRoutes_WhoseIdsSanitizeAlike_KeepTheirOwnHandlers()
    {
        (await _client.GetStringAsync("/api/v/id")).Should().Be("fixed");
        (await _client.GetStringAsync("/api/v/anything")).Should().Be("by-template");
    }

    [Fact]
    public async Task AClientHeaderNamedLikeAQueryParameter_IsNotAQueryParameter()
    {
        using var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, "/api/v/whoami");
        request.Headers.Add("query.role", "admin");

        var response = await _client.SendAsync(request);

        (await response.Content.ReadAsStringAsync()).Should().Be("clean");
    }

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
