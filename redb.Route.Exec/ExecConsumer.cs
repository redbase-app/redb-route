using System.Diagnostics;
using System.Text.RegularExpressions;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Exec;

/// <summary>
/// Scheduled exec consumer. Fires the configured command on a fixed interval (via
/// <see cref="PeriodicTimer"/>) and pushes the resulting exchange into the route pipeline.
/// <para>
/// Supported <c>schedule</c> formats: <c>500ms</c>, <c>30s</c>, <c>5m</c>, <c>1h</c>.
/// Cron expressions are intentionally not handled here — chain
/// <c>From("quartz://&lt;cron&gt;").To("exec://run?command=...")</c> when cron is required.
/// </para>
/// </summary>
public sealed class ExecConsumer : IConsumer
{
    private static readonly Regex IntervalPattern =
        new(@"^\s*(\d+)\s*(ms|s|m|h)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ExecEndpoint _endpoint;
    private readonly ExecEndpointOptions _options;
    private readonly IProcessor _processor;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private ExecProducer? _producer;

    /// <summary>Creates a scheduled exec consumer.</summary>
    public ExecConsumer(ExecEndpoint endpoint, ExecEndpointOptions options, IProcessor processor)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
    }

    /// <inheritdoc />
    public IEndpoint? Endpoint => _endpoint;

    /// <inheritdoc />
    public Task Start(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Schedule))
            throw new InvalidOperationException(
                "Consumer mode for 'exec://' requires '?schedule=' (e.g. '30s', '5m', '1h'). " +
                "For cron expressions chain From(\"quartz://...\").To(\"exec://run?command=...\").");

        if (string.IsNullOrWhiteSpace(_options.Command))
            throw new InvalidOperationException(
                "Consumer mode for 'exec://' requires the URI 'command=' option to be set.");

        var interval = ParseInterval(_options.Schedule)
            ?? throw new InvalidOperationException(
                $"Schedule '{_options.Schedule}' is not a valid interval. " +
                "Accepted formats: '500ms', '30s', '5m', '1h'.");

        _producer = new ExecProducer(_endpoint, _options);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = RunLoopAsync(interval, _cts.Token);
        return _producer.Start(ct);
    }

    /// <inheritdoc />
    public async Task Stop(CancellationToken ct = default)
    {
        if (_cts is null) return;
        try { await _cts.CancelAsync().ConfigureAwait(false); } catch { /* idempotent */ }
        if (_loop is { } loop)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }
        if (_producer is not null)
        {
            try { await _producer.Stop(ct).ConfigureAwait(false); } catch { /* best-effort */ }
            _producer = null;
        }
        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    private async Task RunLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                    return;
            }
            catch (OperationCanceledException) { return; }

            try
            {
                await FireOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _endpoint.RecordError();
                Activity.Current?.AddTag("exec.consumer.error", ex.GetType().Name);
                // Continue — one failed tick must not kill the consumer.
            }
        }
    }

    private async Task FireOnceAsync(CancellationToken ct)
    {
        var ctx = (_endpoint.Component as ComponentBase)?.Context;
        var scopeFactory = ctx?.GetServiceProvider()
            ?.GetService(typeof(Microsoft.Extensions.DependencyInjection.IServiceScopeFactory))
            as Microsoft.Extensions.DependencyInjection.IServiceScopeFactory;

        var exchange = Exchange.Create(new Message(string.Empty), scopeFactory);

        if (!string.IsNullOrWhiteSpace(_options.Command))
            exchange.In.Headers[ExecHeaders.Command] = _options.Command;
        if (!string.IsNullOrWhiteSpace(_options.Args))
            exchange.In.Headers[ExecHeaders.Args] = _options.Args;

        await _producer!.Process(exchange, ct).ConfigureAwait(false);
        await _processor.Process(exchange, ct).ConfigureAwait(false);
    }

    internal static TimeSpan? ParseInterval(string schedule)
    {
        var m = IntervalPattern.Match(schedule);
        if (!m.Success) return null;
        if (!long.TryParse(m.Groups[1].Value, out var n) || n <= 0) return null;
        return m.Groups[2].Value.ToLowerInvariant() switch
        {
            "ms" => TimeSpan.FromMilliseconds(n),
            "s" => TimeSpan.FromSeconds(n),
            "m" => TimeSpan.FromMinutes(n),
            "h" => TimeSpan.FromHours(n),
            _ => null
        };
    }
}
