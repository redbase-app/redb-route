using FluentAssertions;
using NSubstitute;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

public class InflightTrackingProcessorTests
{
    private readonly IInflightRepository _repo = Substitute.For<IInflightRepository>();

    private IProcessor MakeProcessor(IProcessor? inner = null)
        => new InflightTrackingProcessor(inner ?? new DelegateProcessor(_ => { }), _repo, "test-route", "kafka:orders");

    [Fact]
    public async Task Process_RegistersBeforeInner_UnregistersAfter()
    {
        var callOrder = new List<string>();

        _repo.When(r => r.Register(Arg.Any<InflightExchange>()))
             .Do(_ => callOrder.Add("register"));

        _repo.When(r => r.Unregister(Arg.Any<string>()))
             .Do(_ => callOrder.Add("unregister"));

        var inner = new DelegateProcessor(_ => callOrder.Add("inner"));
        var processor = new InflightTrackingProcessor(inner, _repo, "test-route", "kafka:orders");

        await processor.Process(new Exchange(new Message("data")));

        callOrder.Should().Equal("register", "inner", "unregister");
    }

    [Fact]
    public async Task Process_UnregistersOnException()
    {
        var inner = new DelegateProcessor(_ => throw new InvalidOperationException("boom"));
        var processor = new InflightTrackingProcessor(inner, _repo, "test-route");

        var act = () => processor.Process(new Exchange(new Message("data")));
        await act.Should().ThrowAsync<InvalidOperationException>();

        _repo.Received(1).Register(Arg.Any<InflightExchange>());
        _repo.Received(1).Unregister(Arg.Any<string>());
    }

    [Fact]
    public async Task Process_RegistersCorrectFields()
    {
        InflightExchange? captured = null;
        _repo.When(r => r.Register(Arg.Any<InflightExchange>()))
             .Do(ci => captured = ci.Arg<InflightExchange>());

        var exchange = new Exchange(new Message("data"));
        var processor = new InflightTrackingProcessor(
            new DelegateProcessor(_ => { }), _repo, "my-route", "kafka:orders");

        await processor.Process(exchange);

        captured.Should().NotBeNull();
        captured!.ExchangeId.Should().Be(exchange.ExchangeId);
        captured.RouteId.Should().Be("my-route");
        captured.FromEndpoint.Should().Be("kafka:orders");
    }

    [Fact]
    public async Task Process_UnregistersCorrectExchangeId()
    {
        string? unregisteredId = null;
        _repo.When(r => r.Unregister(Arg.Any<string>()))
             .Do(ci => unregisteredId = ci.Arg<string>());

        var exchange = new Exchange(new Message("data"));
        var processor = MakeProcessor();

        await processor.Process(exchange);

        unregisteredId.Should().Be(exchange.ExchangeId);
    }

    [Fact]
    public async Task Process_PropagatesCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken receivedToken = default;

        var inner = new DelegateProcessor((e, ct) =>
        {
            receivedToken = ct;
            return Task.CompletedTask;
        });

        var processor = new InflightTrackingProcessor(inner, _repo, "route");
        await processor.Process(new Exchange(new Message("data")), cts.Token);

        receivedToken.Should().Be(cts.Token);
    }
}
