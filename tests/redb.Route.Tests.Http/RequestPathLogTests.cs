using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using redb.Route.Core;
using redb.Route.Http;

namespace redb.Route.Tests.Http;

/// <summary>
/// A request path with an encoded line break reaches the route (the path is decoded into a route value), yet the
/// consumer's log line carries the path re-escaped: the logged value is the <c>PathString</c>, whose text form is its
/// escaped URI component, so <c>%0D%0A</c> in the request cannot start a new log line.
/// </summary>
public class RequestPathLogTests
{
    [Fact]
    public async Task AnEncodedLineBreakInThePath_IsLoggedEscaped_NotAsALineBreak()
    {
        var capture = new CapturingProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Debug));
        var port = global::redb.Route.Tests.Shared.TestPorts.Next();
        string? routeValue = null;

        await using var server = new SharedHttpServerManager();
        await using var context = new RouteContext(loggerFactory: loggerFactory);
        context.AddComponent(new HttpComponent { ServerManager = server });
        context.AddRoutes(r => r.From($"http://127.0.0.1:{port}/orders/{{id}}").RouteId("path-log")
            .Process((e, _) =>
            {
                routeValue = e.In.getHeader(HttpHeaders.RouteParamPrefix + "id")?.ToString();
                return Task.FromException(new InvalidOperationException("boom"));
            }));
        await context.Start();

        using var client = new HttpClient();
        var response = await client.GetAsync($"http://127.0.0.1:{port}/orders/a%0D%0Aforged");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, "the request passed Kestrel and reached the route");
        routeValue.Should().Be("a\r\nforged", "the route value is decoded, so the line break did reach the server");
        var line = capture.Entries.Single(m => m.StartsWith("Route failed for GET", StringComparison.Ordinal));
        line.Should().Contain("/orders/a%0D%0Aforged").And.NotContain("\r").And.NotContain("\n");
    }

    private sealed class CapturingProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose() { }

        private sealed class Logger(CapturingProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => owner.Entries.Enqueue(formatter(state, exception));
        }
    }
}
