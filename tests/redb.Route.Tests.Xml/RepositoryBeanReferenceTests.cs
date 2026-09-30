using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Processors;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml;

/// <summary>
/// A repository declared as a bean in markup is the one <c>repository="#name"</c> refers to: the name is the registry
/// key as is, for idempotent repositories as for every other reference.
/// </summary>
public class RepositoryBeanReferenceTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_bean_repository_starts_the_route_and_drops_the_repeat()
    {
        var received = new List<object?>();
        _context.AddXmlRoutesFromContent($"""
            <routes xmlns="urn:redb:route:1.0">
              <bean name="dedup" type="{typeof(InMemoryIdempotentRepository).FullName}, {typeof(InMemoryIdempotentRepository).Assembly.GetName().Name}"/>
              <route id="dedup-route">
                <from uri="direct://dedup-in"/>
                <idempotentConsumer key="${"{"}header.id{"}"}" repository="#dedup" skipDuplicate="true">
                  <to uri="mock://dedup-out"/>
                </idempotentConsumer>
              </route>
            </routes>
            """);
        await _context.Start();
        var producer = _context.GetEndpoint("direct://dedup-in").CreateProducer();
        await producer.Start();

        foreach (var _ in new[] { 1, 2 })
        {
            var exchange = new Exchange(new Message("payload"));
            exchange.In.Headers["id"] = "order-1";
            await producer.Process(exchange);
        }

        var mock = (MockEndpoint)_context.GetEndpoint("mock://dedup-out");
        mock.ReceivedExchanges.Should().HaveCount(1, "the repository the bean declared remembers the first id");
    }
}
