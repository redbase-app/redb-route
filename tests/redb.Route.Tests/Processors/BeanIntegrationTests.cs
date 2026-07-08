using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>
/// Integration tests for Bean / Service Activator using full route pipeline with DirectComponent.
/// </summary>
[Trait("Category", "Integration")]
public class BeanIntegrationTests : IAsyncDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly RouteContext _context;

    public BeanIntegrationTests()
    {
        var services = new ServiceCollection()
            .AddSingleton<IOrderService>(new OrderService())
            .AddScoped<IScopedCounter, ScopedCounter>()
            .AddSingleton<IPricingService>(new PricingService());
        _serviceProvider = services.BuildServiceProvider();
        _context = new RouteContext();
    }

    /// <summary>Creates an exchange with DI scope from the test ServiceProvider.</summary>
    private IExchange CreateExchange(object? body = null)
    {
        var factory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        return Exchange.Create(new Message(body), factory);
    }

    // ══════════════════════════════════════════════════════════════
    // Async + CancellationToken — full pipeline
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task AsyncWithCt_ResolvesServiceAndInvokes()
    {
        var def = new RouteDefinition();
        def.From("direct://bean-async-ct");
        def.Bean<IOrderService>(async (svc, exchange, ct) =>
        {
            var input = exchange.In.Body!.ToString()!;
            exchange.In.Body = await svc.ProcessOrderAsync(input, ct);
        });

        var pipeline = def.CreateProcessor(_context);

        var exchange = CreateExchange("order-123");
        await pipeline.Process(exchange);

        exchange.In.Body.Should().Be("processed:order-123");
    }

    // ══════════════════════════════════════════════════════════════
    // Async without CancellationToken
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task AsyncNoCt_ResolvesServiceAndInvokes()
    {
        var def = new RouteDefinition();
        def.From("direct://bean-async");
        def.Bean<IOrderService>(async (svc, exchange) =>
        {
            var input = exchange.In.Body!.ToString()!;
            exchange.In.Body = await svc.ValidateOrderAsync(input);
        });

        var pipeline = def.CreateProcessor(_context);

        var exchange = CreateExchange("order-456");
        await pipeline.Process(exchange);

        exchange.In.Body.Should().Be("valid:order-456");
    }

    // ══════════════════════════════════════════════════════════════
    // Sync — header enrichment
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Sync_ResolvesServiceAndEnrichesHeaders()
    {
        var def = new RouteDefinition();
        def.From("direct://bean-sync");
        def.Bean<IPricingService>((svc, exchange) =>
        {
            var productId = exchange.In.Body!.ToString()!;
            exchange.In.Headers["price"] = svc.GetPrice(productId);
        });

        var pipeline = def.CreateProcessor(_context);

        var exchange = CreateExchange("prod-A");
        await pipeline.Process(exchange);

        exchange.In.Headers["price"].Should().Be(99.99m);
    }

    // ══════════════════════════════════════════════════════════════
    // Missing ServiceProvider — throws
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task NullServiceProvider_ThrowsInvalidOperation()
    {
        var def = new RouteDefinition();
        def.From("direct://bean-no-sp");
        def.Bean<IOrderService>(async (svc, exchange, ct) =>
        {
            exchange.In.Body = await svc.ProcessOrderAsync("x", ct);
        });

        var pipeline = def.CreateProcessor(_context);

        // Exchange without DI scope
        var exchange = Exchange.Create(new Message("test"), null);

        var act = () => pipeline.Process(exchange);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*ServiceProvider*");
    }

    // ══════════════════════════════════════════════════════════════
    // Scoped service — new instance per exchange
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ScopedService_NewInstancePerExchange()
    {
        var def = new RouteDefinition();
        def.From("direct://bean-scoped");
        def.Bean<IScopedCounter>((svc, exchange) =>
        {
            svc.Increment();
            exchange.Properties["count"] = svc.Count;
            exchange.Properties["instanceId"] = svc.InstanceId;
        });

        var pipeline = def.CreateProcessor(_context);

        var ex1 = CreateExchange();
        var ex2 = CreateExchange();

        await pipeline.Process(ex1);
        await pipeline.Process(ex2);

        // Each exchange gets its own scoped instance
        ex1.Properties["count"].Should().Be(1);
        ex2.Properties["count"].Should().Be(1);
        ex1.Properties["instanceId"].Should().NotBe(ex2.Properties["instanceId"]);
    }

    // ══════════════════════════════════════════════════════════════
    // Chained beans — multiple services in pipeline
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ChainedBeans_MultipleServicesInPipeline()
    {
        var def = new RouteDefinition();
        def.From("direct://bean-chain");
        def.Bean<IOrderService>(async (svc, exchange, ct) =>
        {
            exchange.In.Body = await svc.ProcessOrderAsync(exchange.In.Body!.ToString()!, ct);
        });
        def.Bean<IPricingService>((svc, exchange) =>
        {
            exchange.In.Headers["price"] = svc.GetPrice(exchange.In.Body!.ToString()!);
        });

        var pipeline = def.CreateProcessor(_context);

        var exchange = CreateExchange("item-X");
        await pipeline.Process(exchange);

        exchange.In.Body.Should().Be("processed:item-X");
        exchange.In.Headers["price"].Should().Be(99.99m);
    }

    // ══════════════════════════════════════════════════════════════
    // CancellationToken — propagates to service method
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CancellationToken_PropagatedToServiceMethod()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var def = new RouteDefinition();
        def.From("direct://bean-cancel");
        def.Bean<IOrderService>(async (svc, exchange, ct) =>
        {
            exchange.In.Body = await svc.ProcessOrderAsync("x", ct);
        });

        var pipeline = def.CreateProcessor(_context);

        var exchange = CreateExchange("test");

        var act = () => pipeline.Process(exchange, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ══════════════════════════════════════════════════════════════
    // Service throws — exception propagates through pipeline
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ServiceThrows_ExceptionPropagates()
    {
        var def = new RouteDefinition();
        def.From("direct://bean-error");
        def.Bean<IOrderService>(async (svc, exchange, ct) =>
        {
            await svc.FailAsync(ct);
        });

        var pipeline = def.CreateProcessor(_context);

        var exchange = CreateExchange();

        var act = () => pipeline.Process(exchange);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Service failure");
    }

    // ══════════════════════════════════════════════════════════════
    // Bean with downstream To — full route pattern
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task BeanWithDownstream_FullRoutePattern()
    {
        // Register a consumer that captures the output
        string? captured = null;
        var endpoint = (DirectEndpoint)_context.GetEndpoint("direct:bean-sink");
        var consumer = endpoint.CreateConsumer(new DelegateProcessor((e, _) =>
        {
            captured = e.In.Body?.ToString();
            return Task.CompletedTask;
        }));
        await consumer.Start();

        try
        {
            var def = new RouteDefinition();
            def.From("direct://bean-full");
            def.Bean<IOrderService>(async (svc, exchange, ct) =>
            {
                exchange.In.Body = await svc.ProcessOrderAsync(exchange.In.Body!.ToString()!, ct);
            });
            def.To("direct:bean-sink");

            var pipeline = def.CreateProcessor(_context);

            var exchange = CreateExchange("order-789");
            await pipeline.Process(exchange);

            captured.Should().Be("processed:order-789");
        }
        finally
        {
            await consumer.Stop();
        }
    }

    // ══════════════════════════════════════════════════════════════
    // Properties preserved through Bean
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Bean_PreservesExchangeProperties()
    {
        var def = new RouteDefinition();
        def.From("direct://bean-props");
        def.Bean<IOrderService>(async (svc, exchange, ct) =>
        {
            exchange.In.Body = await svc.ProcessOrderAsync(exchange.In.Body!.ToString()!, ct);
        });

        var pipeline = def.CreateProcessor(_context);

        var exchange = CreateExchange("test");
        exchange.Properties["tenant"] = "acme";
        exchange.In.Headers["correlationId"] = "abc-123";

        await pipeline.Process(exchange);

        exchange.Properties["tenant"].Should().Be("acme");
        exchange.In.Headers["correlationId"].Should().Be("abc-123");
        exchange.In.Body.Should().Be("processed:test");
    }

    // ══════════════════════════════════════════════════════════════
    // Dispose
    // ══════════════════════════════════════════════════════════════

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        await _serviceProvider.DisposeAsync();
    }

    // ══════════════════════════════════════════════════════════════
    // Test services
    // ══════════════════════════════════════════════════════════════

    public interface IOrderService
    {
        Task<string> ProcessOrderAsync(string orderId, CancellationToken ct);
        Task<string> ValidateOrderAsync(string orderId);
        Task FailAsync(CancellationToken ct);
    }

    public interface IPricingService
    {
        decimal GetPrice(string productId);
    }

    public interface IScopedCounter
    {
        Guid InstanceId { get; }
        int Count { get; }
        void Increment();
    }

    private sealed class OrderService : IOrderService
    {
        public async Task<string> ProcessOrderAsync(string orderId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            return $"processed:{orderId}";
        }

        public async Task<string> ValidateOrderAsync(string orderId)
        {
            await Task.Yield();
            return $"valid:{orderId}";
        }

        public Task FailAsync(CancellationToken ct)
            => throw new InvalidOperationException("Service failure");
    }

    private sealed class PricingService : IPricingService
    {
        public decimal GetPrice(string productId) => 99.99m;
    }

    private sealed class ScopedCounter : IScopedCounter
    {
        public Guid InstanceId { get; } = Guid.NewGuid();
        public int Count { get; private set; }
        public void Increment() => Count++;
    }
}
