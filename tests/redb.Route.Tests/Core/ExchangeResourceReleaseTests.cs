using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// Resources registered with <see cref="ExchangeResources.ReleaseWithExchange"/> — the counterpart of Apache Camel's
/// <c>addOnCompletion</c> — are released when the exchange ends, most recent first and before its DI scopes, once, and
/// independently of each other; copies of the exchange do not inherit them.
/// </summary>
public sealed class ExchangeResourceReleaseTests
{
    private sealed class TrackingResource(List<string> log, string name, bool fail = false) : IAsyncDisposable
    {
        public int DisposeCount;

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCount);
            lock (log) log.Add(name);
            if (fail)
                throw new InvalidOperationException($"{name}: release failed");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingScope(List<string> log) : IServiceScope, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider => null!;
        public void Dispose()
        {
            lock (log) log.Add("scope");
        }

        public ValueTask DisposeAsync()
        {
            lock (log) log.Add("scope");
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task A_registered_resource_is_released_when_the_exchange_is_disposed()
    {
        var log = new List<string>();
        var exchange = new Exchange(new Message("m"));
        var resource = new TrackingResource(log, "stream");
        ExchangeResources.ReleaseWithExchange(exchange, resource);

        await exchange.DisposeAsync();

        resource.DisposeCount.Should().Be(1);
        exchange.Properties.Keys.Should().NotContain(k => k.StartsWith(ExchangeResources.PropertyPrefix));
    }

    [Fact]
    public async Task Resources_are_released_most_recent_first_and_before_the_exchange_scopes()
    {
        var log = new List<string>();
        var exchange = new Exchange(new Message("m"));
        exchange.Properties["__redb_scope:audit"] = new TrackingScope(log);
        ExchangeResources.ReleaseWithExchange(exchange, new TrackingResource(log, "first"));
        ExchangeResources.ReleaseWithExchange(exchange, new TrackingResource(log, "second"));

        await exchange.ReleaseScopes();

        log.Should().Equal(new[] { "second", "first", "scope" },
            "a later resource may depend on an earlier one, and a resource may come from a factory living in a scope");
    }

    [Fact]
    public async Task A_failing_release_does_not_stop_the_other_releases()
    {
        var log = new List<string>();
        var exchange = new Exchange(new Message("m"));
        var healthy = new TrackingResource(log, "healthy");
        ExchangeResources.ReleaseWithExchange(exchange, healthy);
        ExchangeResources.ReleaseWithExchange(exchange, new TrackingResource(log, "broken", fail: true));

        await exchange.DisposeAsync();

        healthy.DisposeCount.Should().Be(1);
        log.Should().Equal("broken", "healthy");
    }

    [Fact]
    public async Task Releasing_twice_releases_each_resource_once()
    {
        var exchange = new Exchange(new Message("m"));
        var resource = new TrackingResource([], "stream");
        ExchangeResources.ReleaseWithExchange(exchange, resource);

        await exchange.ReleaseScopes();
        await exchange.DisposeAsync();

        resource.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task Resources_handed_over_are_released_with_the_receiving_exchange()
    {
        var resourceExchange = new Exchange(new Message("resource"));
        var original = new Exchange(new Message("original"));
        var resource = new TrackingResource([], "stream");
        ExchangeResources.ReleaseWithExchange(resourceExchange, resource);

        ExchangeResources.HandOver(resourceExchange, original);
        await resourceExchange.DisposeAsync();

        resource.DisposeCount.Should().Be(0, "the resource now belongs to the receiving exchange (Apache Camel: handoverCompletions)");
        resourceExchange.Properties.Keys.Should().NotContain(k => k.StartsWith(ExchangeResources.PropertyPrefix));
        await original.DisposeAsync();
        resource.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task A_failing_release_is_logged_through_the_context_when_the_exchange_has_no_scope()
    {
        var logs = new CapturingLoggerFactory();
        await using var context = new RouteContext(loggerFactory: logs);
        var exchange = new Exchange(new Message("m")) { Context = context };
        ExchangeResources.ReleaseWithExchange(exchange, new TrackingResource([], "broken", fail: true));

        await exchange.DisposeAsync();

        logs.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.Contains("Releasing resource"),
            "an exchange without a DI scope (ProducerTemplate, tests) still has a context to log through; " +
            $"logged: {string.Join(" | ", logs.Entries.Select(e => e.Message))}");
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class CapturingLogger(CapturingLoggerFactory owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner.Entries)
                    owner.Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    [Fact]
    public void Copies_of_the_exchange_do_not_inherit_registered_resources()
    {
        var exchange = new Exchange(new Message("m"));
        ExchangeResources.ReleaseWithExchange(exchange, new TrackingResource([], "stream"));
        exchange.Properties["user"] = "kept";

        var copies = new[]
        {
            exchange.CreateChild(new Message("child")),
            exchange.CreateLinkedChild(new Message("linked")),
            exchange.Clone(),
            exchange.CloneLinked(),
            exchange.Snapshot(),
        };

        foreach (var copy in copies)
        {
            copy.Properties.Should().ContainKey("user");
            copy.Properties.Keys.Should().NotContain(k => k.StartsWith(ExchangeResources.PropertyPrefix),
                "a resource belongs to the exchange that registered it; a copy releasing it would close it under the original");
        }
    }
}
