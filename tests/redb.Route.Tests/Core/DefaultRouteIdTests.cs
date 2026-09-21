using FluentAssertions;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// The id of a route that declared none. It used to be the sanitized endpoint URI, so every
/// <c>{RouteId}</c> log line, metric tag and dashboard label carried a <c>://</c>; now it is the
/// scheme, the path and a deterministic UUID of the endpoint: readable, the same on every start and
/// every node, and free of both URI separators and secrets.
/// </summary>
public class DefaultRouteIdTests
{
    private static async Task<string> IdOf(string fromUri)
    {
        await using var context = new RouteContext();
        context.AddRoutes(r => r.From(fromUri).Process(_ => { }));
        await context.Start();
        return context.Routes.Single().RouteId;
    }

    [Fact]
    public async Task The_scheme_and_path_lead_and_a_uuid_closes()
    {
        var id = await IdOf("direct://orders");

        id.Should().StartWith("direct-orders-");
        Guid.TryParse(id["direct-orders-".Length..], out _).Should().BeTrue("the tail is a UUID");
    }

    [Fact]
    public async Task The_id_holds_nothing_a_dashboard_chokes_on()
    {
        var id = await IdOf("seda://orders/eu-west?size=10");

        id.Should().StartWith("seda-orders-eu-west-");
        id.Should().NotContainAny("/", ":", "?", "&", "=", " ");
    }

    [Fact]
    public async Task A_secret_in_the_uri_never_reaches_the_id()
    {
        var id = await IdOf("direct://svc?password=topsecret");

        id.Should().Be("direct-svc-" + RouteIdFactory.Uuid5(
            RouteIdFactory.UrlNamespace, "direct://svc?password=topsecret"));
        id.Should().NotContain("topsecret", "the query is hashed, not printed");
        id.Should().NotContain("****", "there is no masked leftover to print either");
    }

    [Fact]
    public async Task The_same_endpoint_keeps_its_id_across_restarts()
    {
        var first = await IdOf("direct://stable");
        var second = await IdOf("direct://stable");

        // Metrics, checkpoints and control-bus commands address a route by this id: a restart,
        // another node or another process must produce the very same one.
        second.Should().Be(first);
    }

    [Fact]
    public void Endpoints_that_differ_only_in_a_parameter_get_different_ids()
    {
        var withOneSecret = RouteIdFactory.ForEndpoint(EndpointUriParser.Parse("sql://db?password=one"));
        var withAnother = RouteIdFactory.ForEndpoint(EndpointUriParser.Parse("sql://db?password=two"));

        // Sanitizing masked both to "sql://db?password=****" and the second route was refused as a
        // duplicate id although the URIs differ; the UUID sees the values the label never shows.
        withOneSecret.Should().NotBe(withAnother);
    }

    [Fact]
    public void Credentials_in_the_authority_are_dropped_from_the_readable_part()
    {
        var id = RouteIdFactory.ForEndpoint(EndpointUriParser.Parse("amqp://user:pw@localhost:5672/orders"));

        id.Should().StartWith("amqp-localhost-5672-orders-");
        id.Should().NotContain("pw").And.NotContain("user");
    }

    [Fact]
    public async Task A_long_path_is_cut_short_and_the_uuid_still_tells_routes_apart()
    {
        var shared = new string('a', 60);
        var first = await IdOf($"direct://{shared}-one");
        var second = await IdOf($"direct://{shared}-two");

        first.Length.Should().BeLessThan(90, "the readable part is capped, the UUID is not");
        first.Should().NotBe(second);
    }

    [Fact]
    public async Task A_declared_route_id_is_left_alone()
    {
        await using var context = new RouteContext();
        context.AddRoutes(r => r.From("direct://named").RouteId("orders-in").Process(_ => { }));
        await context.Start();

        context.Routes.Single().RouteId.Should().Be("orders-in");
    }

    [Fact]
    public void The_uuid_is_the_one_any_uuid_library_computes()
    {
        // RFC 4122 version 5 over the DNS namespace, the vector published with Python's uuid module:
        // proof that the id can be reproduced outside this code.
        RouteIdFactory.Uuid5(new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8"), "python.org")
            .Should().Be(new Guid("886313e1-3b8a-5372-9b90-0c9aee199e5d"));
    }
}
