using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;

namespace redb.Route.Tests.Components;

/// <summary>
/// Every component built into redb.Route keeps working after <c>Stop()</c> and <c>Start()</c> of the same route context.
/// The context keeps its components and their cached endpoints across the restart, so state an endpoint holds (a queue,
/// a compiled schema or stylesheet, a timer, a reference to a route) must not be closed for good by the stop (redb-route
/// issue #12 was <c>seda:</c>).
/// </summary>
public sealed class BuiltInComponentRestartTests : IAsyncDisposable
{
    private readonly RouteContext _context;
    private readonly string _dir = Directory.CreateTempSubdirectory("redb-restart-").FullName;

    public BuiltInComponentRestartTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new SharedVmRegistry());
        _context = new RouteContext(services.BuildServiceProvider(), "restart");
    }

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    private async Task<IExchange> SendAsync(string uri, object body)
    {
        var producer = _context.GetEndpoint(uri).CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message { Body = body });
        await producer.Process(exchange);
        return exchange;
    }

    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        condition().Should().BeTrue(because);
    }

    /// <summary>Runs <paramref name="check"/>, restarts the context, and runs it again.</summary>
    private async Task BeforeAndAfterRestartAsync(Func<int, Task> check)
    {
        await _context.Start();
        await check(1);
        await _context.Stop();
        await _context.Start();
        await check(2);
    }

    [Fact]
    public async Task Direct()
    {
        var received = 0;
        _context.AddRoutes(r => r.From("direct://in").Process(_ => Interlocked.Increment(ref received)));

        await BeforeAndAfterRestartAsync(async round =>
        {
            await SendAsync("direct://in", "x");
            Volatile.Read(ref received).Should().Be(round, "direct после рестарта доставляет");
        });
    }

    [Fact]
    public async Task Seda()
    {
        var received = 0;
        _context.AddRoutes(r => r.From("seda://in").Process(_ => Interlocked.Increment(ref received)));

        await BeforeAndAfterRestartAsync(async round =>
        {
            await SendAsync("seda://in", "x");
            await WaitForAsync(() => Volatile.Read(ref received) == round, "seda после рестарта доставляет");
        });
    }

    [Fact]
    public async Task Vm()
    {
        var received = 0;
        _context.AddRoutes(r => r.From("vm://in").Process(_ => Interlocked.Increment(ref received)));

        await BeforeAndAfterRestartAsync(async round =>
        {
            await SendAsync("vm://in", "x");
            await WaitForAsync(() => Volatile.Read(ref received) == round, "vm после рестарта доставляет");
        });
    }

    [Fact]
    public async Task DirectVm()
    {
        var received = 0;
        _context.AddRoutes(r => r.From("direct-vm://in").Process(_ => Interlocked.Increment(ref received)));

        await BeforeAndAfterRestartAsync(async round =>
        {
            await SendAsync("direct-vm://in", "x");
            Volatile.Read(ref received).Should().Be(round, "direct-vm после рестарта доставляет");
        });
    }

    [Fact]
    public async Task Timer()
    {
        var fired = 0;
        _context.AddRoutes(r => r.From("timer://tick?period=50").Process(_ => Interlocked.Increment(ref fired)));

        await BeforeAndAfterRestartAsync(async _ =>
        {
            var before = Volatile.Read(ref fired);
            await WaitForAsync(() => Volatile.Read(ref fired) > before, "timer после рестарта срабатывает");
        });
    }

    [Fact]
    public async Task Log()
    {
        var after = 0;
        _context.AddRoutes(r => r.From("direct://log").To("log://restart?showBody=true")
            .Process(_ => Interlocked.Increment(ref after)));

        await BeforeAndAfterRestartAsync(async round =>
        {
            await SendAsync("direct://log", "x");
            Volatile.Read(ref after).Should().Be(round, "log после рестарта пропускает обмен дальше");
        });
    }

    [Fact]
    public async Task Mock()
    {
        _context.AddRoutes(r => r.From("direct://mock").To("mock://result"));

        await BeforeAndAfterRestartAsync(async round =>
        {
            await SendAsync("direct://mock", "x");
            ((MockEndpoint)_context.GetEndpoint("mock://result")).ReceivedCount.Should().Be(round,
                "mock после рестарта принимает");
        });
    }

    public sealed class Greeter
    {
        public void Greet(IExchange exchange) => exchange.In.Body = "hi " + exchange.In.Body;
    }

    [Fact]
    public async Task Bean()
    {
        _context.AddToRegistry("greeter", new Greeter());
        _context.AddRoutes(r => r.From("direct://bean").To("bean:#greeter?method=Greet"));

        await BeforeAndAfterRestartAsync(async _ =>
            (await SendAsync("direct://bean", "bob")).In.Body.Should().Be("hi bob", "bean после рестарта вызывается"));
    }

    [Fact]
    public async Task ControlBus()
    {
        _context.AddRoutes(r =>
        {
            r.From("direct://worker").RouteId("worker").Process(_ => { });
            r.From("direct://control").To("controlbus:route?routeId=worker&action=status");
        });

        await BeforeAndAfterRestartAsync(async _ =>
            (await SendAsync("direct://control", "x")).In.Body.Should().Be("Started",
                "controlbus после рестарта видит маршрут нового запуска"));
    }

    [Fact]
    public async Task Validator()
    {
        var schema = Path.Combine(_dir, "person.json");
        await File.WriteAllTextAsync(schema,
            """{ "type": "object", "required": ["name"], "properties": { "name": { "type": "string" } } }""");
        _context.AddComponent(new redb.Route.Validation.ValidatorComponent());
        _context.AddRoutes(r => r.From("direct://validate").To($"validator:{schema}"));

        await BeforeAndAfterRestartAsync(async _ =>
        {
            (await SendAsync("direct://validate", """{ "name": "bob" }""")).Exception.Should().BeNull(
                "validator после рестарта пропускает верный документ");
            var invalid = () => SendAsync("direct://validate", """{ "age": 3 }""");
            await invalid.Should().ThrowAsync<redb.Route.Validation.ValidationException>(
                "validator после рестарта отклоняет неверный документ");
        });
    }

    [Fact]
    public async Task Xslt()
    {
        var stylesheet = Path.Combine(_dir, "upper.xslt");
        await File.WriteAllTextAsync(stylesheet, """
            <xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:template match="/a"><b><xsl:value-of select="."/></b></xsl:template>
            </xsl:stylesheet>
            """);
        _context.AddRoutes(r => r.From("direct://xslt").To($"xslt:{stylesheet}"));

        await BeforeAndAfterRestartAsync(async _ =>
            (await SendAsync("direct://xslt", "<a>x</a>")).In.Body?.ToString().Should().Be("<b>x</b>",
                "xslt после рестарта преобразует"));
    }
}
