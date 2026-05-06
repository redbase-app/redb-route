using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Components;

/// <summary>
/// Timer component. Scheme: "timer".
/// Generates exchanges on a periodic schedule. Used as a route source (From).
/// URI: timer://name?period=1000&amp;delay=0&amp;repeatCount=-1
/// </summary>
public class TimerComponent : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "timer";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var options = new TimerEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Validate();
        return new TimerEndpoint(uri, this, options);
    }
}

/// <summary>
/// Timer endpoint options.
/// </summary>
public class TimerEndpointOptions : EndpointOptions
{
    /// <summary>Period between fires in milliseconds (default: 1000).</summary>
    public int Period { get; set; } = 1000;

    /// <summary>Initial delay before first fire in milliseconds (default: 0).</summary>
    public int Delay { get; set; }

    /// <summary>Number of times to fire. -1 means indefinitely (default: -1).</summary>
    public int RepeatCount { get; set; } = -1;

    /// <inheritdoc />
    public override void Validate()
    {
        if (Period <= 0)
            throw new ArgumentOutOfRangeException(nameof(Period), Period, "Period must be greater than 0.");
    }
}

/// <summary>
/// Timer endpoint. The consumer is the primary artifact — a timer that fires periodically.
/// </summary>
public class TimerEndpoint : EndpointBase<TimerEndpointOptions>
{
    /// <summary>Creates a timer endpoint.</summary>
    public TimerEndpoint(EndpointUri uri, TimerComponent component, TimerEndpointOptions options)
        : base(uri, component, options)
    {
    }

    /// <inheritdoc />
    public override IProducer CreateProducer()
    {
        throw new NotSupportedException("Timer endpoints do not support producers. Use them only as From() source.");
    }

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        return new TimerConsumer(this, processor, Options);
    }
}

/// <summary>
/// Timer consumer. Fires the processor on a periodic schedule.
/// Creates a new exchange per tick.
/// </summary>
public class TimerConsumer : DrainableConsumer
{
    private readonly TimerEndpoint _endpoint;
    private readonly TimerEndpointOptions _options;

    /// <inheritdoc />
    protected override IEndpoint ConsumerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ConsumerName => _endpoint.Uri.NormalizedKey;

    /// <summary>Gets the number of fires completed.</summary>
    public long FireCount { get; private set; }

    /// <summary>Creates a timer consumer.</summary>
    public TimerConsumer(TimerEndpoint endpoint, IProcessor processor, TimerEndpointOptions options)
        : base(processor)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    protected override async Task RunAsync(CancellationToken pollCt, CancellationToken processingCt)
    {
        if (_options.Delay > 0)
            await Task.Delay(_options.Delay, pollCt).ConfigureAwait(false);

        var period = TimeSpan.FromMilliseconds(_options.Period);
        var count = 0;

        while (!pollCt.IsCancellationRequested)
        {
            if (_options.RepeatCount >= 0 && count >= _options.RepeatCount)
                break;

            var exchange = CreateTimerExchange(count);
            await ProcessWithTracking(exchange, processingCt).ConfigureAwait(false);

            FireCount++;
            count++;

            if (_options.RepeatCount >= 0 && count >= _options.RepeatCount)
                break;

            try
            {
                await Task.Delay(period, pollCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (pollCt.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private Exchange CreateTimerExchange(int fireIndex)
    {
        var message = new Message
        {
            Body = null,
            Headers =
            {
                ["CamelTimerName"] = _endpoint.Uri.Path,
                ["CamelTimerFiredTime"] = DateTimeOffset.UtcNow,
                ["CamelTimerCounter"] = fireIndex
            }
        };
        var exchange = Exchange.Create(message, _endpoint.ScopeFactory);
        exchange.Pattern = ExchangePattern.InOnly;
        return exchange;
    }
}
