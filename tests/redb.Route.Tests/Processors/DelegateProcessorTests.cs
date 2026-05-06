using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>Tests for <see cref="DelegateProcessor"/>.</summary>
public class DelegateProcessorTests
{
    /// <summary>Async delegate receives the exchange.</summary>
    [Fact]
    public async Task Process_AsyncDelegate_Executes()
    {
        IExchange? captured = null;
        var processor = new DelegateProcessor(async (ex, ct) =>
        {
            captured = ex;
            await Task.CompletedTask;
        });

        var exchange = new Exchange(new Message("hello"));
        await processor.Process(exchange);

        captured.Should().BeSameAs(exchange);
    }

    /// <summary>Sync delegate receives the exchange.</summary>
    [Fact]
    public async Task Process_SyncDelegate_Executes()
    {
        object? capturedBody = null;
        var processor = new DelegateProcessor(ex =>
        {
            capturedBody = ex.In.Body;
        });

        var exchange = new Exchange(new Message("test"));
        await processor.Process(exchange);

        capturedBody.Should().Be("test");
    }

    /// <summary>Delegate can modify the exchange body.</summary>
    [Fact]
    public async Task Process_CanModifyExchange()
    {
        var processor = new DelegateProcessor(ex =>
        {
            ex.In.Body = "modified";
        });

        var exchange = new Exchange(new Message("original"));
        await processor.Process(exchange);

        exchange.In.Body.Should().Be("modified");
    }

    /// <summary>Null async delegate throws ArgumentNullException.</summary>
    [Fact]
    public void Constructor_NullAsyncDelegate_Throws()
    {
        var act = () => new DelegateProcessor((Func<IExchange, CancellationToken, Task>)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>Null sync delegate throws ArgumentNullException.</summary>
    [Fact]
    public void Constructor_NullSyncDelegate_Throws()
    {
        var act = () => new DelegateProcessor((Action<IExchange>)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>CancellationToken is passed through to the delegate.</summary>
    [Fact]
    public async Task Process_CancellationToken_PassedThrough()
    {
        CancellationToken capturedToken = default;
        var processor = new DelegateProcessor((ex, ct) =>
        {
            capturedToken = ct;
            return Task.CompletedTask;
        });

        using var cts = new CancellationTokenSource();
        var exchange = new Exchange();
        await processor.Process(exchange, cts.Token);

        capturedToken.Should().Be(cts.Token);
    }
}
