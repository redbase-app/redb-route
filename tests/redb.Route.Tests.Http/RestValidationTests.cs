using System.Net;
using System.Text;
using System.Text.Json;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;
using redb.Route.Http.Rest;

namespace redb.Route.Tests.Http;

/// <summary>
/// Client request validation of the REST DSL, the counterpart of Camel's
/// <c>clientRequestValidation</c>: 406 when <c>Accept</c> excludes what the operation produces,
/// 400 when a declared parameter is missing or does not convert to its declared type. Switched on by
/// an explicit option only; parameters declared just for the OpenAPI document change nothing without
/// it. The error body is written by a registry processor when one is named.
/// </summary>
[Collection("HttpServer")]
public class RestValidationTests : IAsyncLifetime
{
    /// <summary>Writes a JSON error body from the properties the REST step leaves on the exchange.</summary>
    private sealed class JsonErrors : IProcessor
    {
        public Task Process(IExchange exchange, CancellationToken ct = default)
        {
            var code = exchange.Properties[RestErrorProperties.Code];
            exchange.Properties.TryGetValue(RestErrorProperties.Parameter, out var parameter);
            exchange.In.Headers[HttpHeaders.ResponseContentType] = "application/problem+json";
            exchange.In.Body = JsonSerializer.Serialize(new
            {
                status = code,
                detail = exchange.Properties[RestErrorProperties.Reason],
                parameter,
            });
            return Task.CompletedTask;
        }
    }

    private sealed class Api(int port) : RouteBuilder
    {
        protected override void Configure()
        {
            var strict = this.Rest("/strict", o =>
            {
                o.Host = "127.0.0.1"; o.Port = port; o.ClientRequestValidation = true;
            });
            strict.Get("/items/{id}").Produces("application/json")
                .Param("id", RestParamType.Path, dataType: RestParamDataType.Integer)
                .Param("limit", RestParamType.Query, required: true, dataType: RestParamDataType.Integer, description: "Page size")
                .Param("verbose", RestParamType.Query, dataType: RestParamDataType.Boolean)
                .Param("X-Tenant", RestParamType.Header, required: true)
                .Route().SetBody("{\"ok\":true}");
            strict.Get("/any").Route().SetBody("no produces, any Accept");
            strict.Post("/orders").Consumes("application/json").Route().SetBody("created");

            // The same declarations without the option: described in OpenAPI, never enforced.
            var loose = this.Rest("/loose", o => { o.Host = "127.0.0.1"; o.Port = port; });
            loose.Get("/items").Produces("application/json")
                .Param("limit", RestParamType.Query, required: true, dataType: RestParamDataType.Integer)
                .Route().SetBody("{\"ok\":true}");
            loose.Get("/checked").Produces("application/json").ClientRequestValidation()
                .Route().SetBody("{\"ok\":true}");

            var custom = this.Rest("/custom", o =>
            {
                o.Host = "127.0.0.1"; o.Port = port; o.ClientRequestValidation = true; o.ErrorHandler = "#jsonErrors";
            });
            custom.Get("/items").Produces("application/json")
                .Param("limit", RestParamType.Query, required: true, dataType: RestParamDataType.Integer)
                .Route().SetBody("{\"ok\":true}");
            custom.Post("/orders").Consumes("application/json").Route().SetBody("created");
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
        _ctx.AddToRegistry("jsonErrors", new JsonErrors());
        _ctx.AddRoutes(new Api(_port));
        await _ctx.Start();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _ctx.DisposeAsync();
        await _serverManager.DisposeAsync();
    }

    private Task<HttpResponseMessage> Get(string url, string? accept = null, string? tenant = "acme")
    {
        var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
        if (accept is not null) request.Headers.TryAddWithoutValidation("Accept", accept);
        if (tenant is not null) request.Headers.Add("X-Tenant", tenant);
        return _client.SendAsync(request);
    }

    // ── 406: Accept against produces ─────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("*/*")]
    [InlineData("application/*")]
    [InlineData("application/json")]
    [InlineData("text/html, application/json;q=0.5")]
    [InlineData("application/json;q=0.2, */*;q=0")]
    public async Task An_accept_that_admits_the_produced_type_passes(string? accept)
    {
        var response = await Get("/strict/items/7?limit=10", accept);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("application/xml")]
    [InlineData("text/*")]
    [InlineData("application/json;q=0")]
    [InlineData("*/*, application/json;q=0")]
    public async Task An_accept_that_excludes_the_produced_type_is_406(string accept)
    {
        var response = await Get("/strict/items/7?limit=10", accept);

        response.StatusCode.Should().Be(HttpStatusCode.NotAcceptable);
        (await response.Content.ReadAsStringAsync()).Should().Contain("application/json");
    }

    [Fact]
    public async Task An_operation_without_produces_accepts_any_accept()
    {
        var response = await Get("/strict/any", "application/xml");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── 400: declared parameters ─────────────────────────────────────────────

    [Fact]
    public async Task A_missing_required_query_parameter_is_400_naming_it()
    {
        var response = await Get("/strict/items/7");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("limit");
    }

    [Fact]
    public async Task A_missing_required_header_is_400_naming_it()
    {
        var response = await Get("/strict/items/7?limit=10", tenant: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("X-Tenant");
    }

    [Theory]
    [InlineData("/strict/items/7?limit=ten", "limit")]
    [InlineData("/strict/items/seven?limit=10", "id")]
    [InlineData("/strict/items/7?limit=10&verbose=maybe", "verbose")]
    public async Task A_value_that_does_not_convert_to_the_declared_type_is_400_naming_it(string url, string parameter)
    {
        var response = await Get(url);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(parameter);
    }

    [Fact]
    public async Task An_optional_parameter_may_be_absent()
    {
        var response = await Get("/strict/items/7?limit=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Consumes_is_still_checked_first_with_415()
    {
        var response = await _client.PostAsync("/strict/orders", new StringContent("<o/>", Encoding.UTF8, "application/xml"));

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    // ── The option ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Without_the_option_declared_parameters_and_accept_are_not_enforced()
    {
        var response = await Get("/loose/items", "application/xml");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_verb_can_switch_validation_on_by_itself()
    {
        var response = await Get("/loose/checked", "application/xml");

        response.StatusCode.Should().Be(HttpStatusCode.NotAcceptable);
    }

    // ── Error format ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_named_error_handler_writes_the_body_with_code_reason_and_parameter()
    {
        var response = await Get("/custom/items");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("status").GetInt32().Should().Be(400);
        body.RootElement.GetProperty("parameter").GetString().Should().Be("limit");
        body.RootElement.GetProperty("detail").GetString().Should().Contain("limit");
    }

    [Fact]
    public async Task The_error_handler_also_answers_415()
    {
        var response = await _client.PostAsync("/custom/orders", new StringContent("<o/>", Encoding.UTF8, "application/xml"));

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Without_an_error_handler_the_body_stays_plain_text()
    {
        var response = await Get("/strict/items/7");

        response.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
    }

    [Fact]
    public async Task An_unknown_error_handler_fails_the_start_not_the_first_request()
    {
        await using var ctx = new RouteContext();
        await using var servers = new SharedHttpServerManager();
        ctx.AddComponent(new HttpComponent { ServerManager = servers });
        var port = GetFreePort();
        ctx.AddRoutes(b => b.Rest("/x", o => { o.Host = "127.0.0.1"; o.Port = port; o.ErrorHandler = "#nobody"; })
            .Get("/y").Route().SetBody("y"));

        var act = () => ctx.Start();

        (await act.Should().ThrowAsync<Exception>()).Which.ToString().Should().Contain("nobody");
    }

    // ── Declaration errors ───────────────────────────────────────────────────

    [Fact]
    public void A_path_parameter_that_is_not_in_the_template_is_refused()
    {
        var rest = Declaration("/api");

        var act = () => rest.Get("/{id}").Param("code", RestParamType.Path);

        act.Should().Throw<ArgumentException>().WithMessage("*code*");
    }

    [Fact]
    public void A_path_parameter_cannot_be_optional()
    {
        var rest = Declaration("/api");

        var act = () => rest.Get("/{id}").Param("id", RestParamType.Path, required: false);

        act.Should().Throw<ArgumentException>().WithMessage("*required*");
    }

    [Fact]
    public void A_parameter_declared_twice_is_refused()
    {
        var rest = Declaration("/api");

        var act = () => rest.Get("").Param("limit").Param("limit");

        act.Should().Throw<ArgumentException>().WithMessage("*limit*");
    }

    // ── OpenAPI ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenApi_lists_the_declared_parameters()
    {
        using var doc = JsonDocument.Parse(await _client.GetStringAsync("/strict/openapi.json"));
        var parameters = doc.RootElement.GetProperty("paths").GetProperty("/strict/items/{id}")
            .GetProperty("get").GetProperty("parameters").EnumerateArray()
            .ToDictionary(p => p.GetProperty("name").GetString()!);

        parameters.Keys.Should().BeEquivalentTo(["id", "limit", "verbose", "X-Tenant"]);
        parameters["id"].GetProperty("in").GetString().Should().Be("path");
        parameters["id"].GetProperty("schema").GetProperty("type").GetString().Should().Be("integer");
        parameters["limit"].GetProperty("in").GetString().Should().Be("query");
        parameters["limit"].GetProperty("required").GetBoolean().Should().BeTrue();
        parameters["limit"].GetProperty("description").GetString().Should().Be("Page size");
        parameters["verbose"].GetProperty("required").GetBoolean().Should().BeFalse();
        parameters["verbose"].GetProperty("schema").GetProperty("type").GetString().Should().Be("boolean");
        parameters["X-Tenant"].GetProperty("in").GetString().Should().Be("header");
    }

    private static int GetFreePort() => global::redb.Route.Tests.Shared.TestPorts.Next();

    /// <summary>A REST declaration for tests that only inspect the declaration itself.</summary>
    private static RestDefinition Declaration(string basePath)
        => new InlineRouteBuilder(_ => { }).Rest(basePath, o => o.OpenApi = false);
}
