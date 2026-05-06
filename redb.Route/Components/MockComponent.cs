using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Components;

/// <summary>
/// Mock component for testing. Scheme: "mock".
/// Captures all received exchanges so tests can assert on them.
/// Supports count expectations, latch-based waiting, and body predicates.
/// URI: mock://name?expectedMessageCount=0
/// </summary>
public class MockComponent : ComponentBase
{
    private readonly ConcurrentDictionary<string, MockEndpoint> _endpoints = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override string Scheme => "mock";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var options = new MockEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Validate();
        return _endpoints.GetOrAdd(uri.NormalizedKey, _ => new MockEndpoint(uri, this, options));
    }

    /// <summary>
    /// Gets a mock endpoint by name (e.g., "result" for "mock://result").
    /// Returns null if not found.
    /// </summary>
    /// <param name="name">Endpoint name (path portion of the URI).</param>
    /// <returns>Mock endpoint or null.</returns>
    public MockEndpoint? GetEndpoint(string name)
    {
        var key = $"mock://{name}";
        return _endpoints.TryGetValue(key, out var ep) ? ep : null;
    }
}

/// <summary>
/// Options for mock endpoints.
/// </summary>
public class MockEndpointOptions : EndpointOptions
{
    /// <summary>Expected number of messages. 0 = no expectation (default: 0).</summary>
    public int ExpectedMessageCount { get; set; }

    /// <inheritdoc />
    public override void Validate() { }
}

/// <summary>
/// Mock endpoint. Captures received exchanges for testing.
/// </summary>
public class MockEndpoint : EndpointBase<MockEndpointOptions>
{
    private readonly ConcurrentQueue<IExchange> _received = new();
    private readonly SemaphoreSlim _latch = new(0);
    private readonly MockEndpointOptions _mockOptions;

    /// <summary>Creates a mock endpoint.</summary>
    public MockEndpoint(EndpointUri uri, MockComponent component, MockEndpointOptions options)
        : base(uri, component, options)
    {
        _mockOptions = options;
    }

    /// <summary>Gets all received exchanges in order.</summary>
    public IReadOnlyList<IExchange> ReceivedExchanges => [.. _received];

    /// <summary>Gets the number of received exchanges.</summary>
    public int ReceivedCount => _received.Count;

    /// <summary>Gets the expected message count (from options).</summary>
    public int ExpectedMessageCount => _mockOptions.ExpectedMessageCount;

    /// <summary>
    /// Waits until the expected number of messages are received, or until timeout.
    /// </summary>
    /// <param name="timeout">Maximum wait time.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the expected count was reached; false on timeout.</returns>
    public async Task<bool> WaitForSatisfied(TimeSpan timeout, CancellationToken ct = default)
    {
        if (_mockOptions.ExpectedMessageCount <= 0)
            return true;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            while (ReceivedCount < _mockOptions.ExpectedMessageCount)
            {
                await _latch.WaitAsync(cts.Token).ConfigureAwait(false);
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            return ReceivedCount >= _mockOptions.ExpectedMessageCount;
        }
    }

    /// <summary>Resets all captured exchanges and the latch counter.</summary>
    public void Reset()
    {
        while (_received.TryDequeue(out _)) { }
        // Drain semaphore
        while (_latch.CurrentCount > 0)
            _latch.Wait(0);
    }

    /// <summary>Records an exchange (called by MockProducer/MockConsumer).</summary>
    internal void RecordExchange(IExchange exchange)
    {
        _received.Enqueue(exchange);
        _latch.Release();
    }

    /// <inheritdoc />
    public override IProducer CreateProducer() => new MockProducer(this);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        return new MockConsumer(this, processor);
    }
}

/// <summary>
/// Mock producer. Records the exchange and optionally invokes a registered consumer.
/// </summary>
public class MockProducer : IProducer
{
    private readonly MockEndpoint _endpoint;
    private ILogger? _logger;

    /// <summary>Creates a mock producer.</summary>
    public MockProducer(MockEndpoint endpoint)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _logger = (endpoint.Component as ComponentBase)?.Logger;
    }

    /// <inheritdoc />
    public Task Process(IExchange exchange, CancellationToken ct = default)
    {
        _endpoint.RecordExchange(exchange);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Start(CancellationToken ct = default)
    {
        _logger?.LogInformation("Mock producer started: {Name}", _endpoint.Uri.NormalizedKey);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Stop(CancellationToken ct = default)
    {
        _logger?.LogInformation("Mock producer stopped: {Name}", _endpoint.Uri.NormalizedKey);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Mock consumer. Delegates to the inner processor and records the exchange.
/// </summary>
public class MockConsumer : IConsumer
{
    private readonly MockEndpoint _endpoint;
    public IEndpoint Endpoint => _endpoint;
    private readonly IProcessor _processor;
    private ILogger? _logger;

    /// <summary>Creates a mock consumer.</summary>
    public MockConsumer(MockEndpoint endpoint, IProcessor processor)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _logger = (endpoint.Component as ComponentBase)?.Logger;
    }

    /// <inheritdoc />
    public Task Start(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task Stop(CancellationToken ct = default) => Task.CompletedTask;
}
