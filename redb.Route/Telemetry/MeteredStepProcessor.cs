using System.Diagnostics;
using redb.Route.Abstractions;

namespace redb.Route.Telemetry;

/// <summary>
/// Wraps a processor pipeline to collect per-step metrics: count, duration, failures.
/// Unlike <see cref="MeteredProcessor"/> (route-level), this tags metrics with
/// both <c>redb.route.id</c> and <c>redb.route.step</c>.
/// </summary>
public sealed class MeteredStepProcessor : IProcessor
{
    private readonly IProcessor _inner;
    private readonly string _stepName;
    private readonly IReadOnlyList<(string Name, Func<IExchange, object?> Resolver)>? _tagProviders;

    /// <summary>Creates a per-step metered processor wrapper.</summary>
    /// <param name="inner">Inner processor (the metered sub-pipeline).</param>
    /// <param name="stepName">Static step name for metric tags.</param>
    public MeteredStepProcessor(IProcessor inner, string stepName)
        : this(inner, stepName, null) { }

    /// <summary>Creates a per-step metered processor wrapper with dynamic tag providers.</summary>
    /// <param name="inner">Inner processor (the metered sub-pipeline).</param>
    /// <param name="stepName">Static step name for metric tags.</param>
    /// <param name="tagProviders">Optional providers evaluated per message for additional metric dimensions.</param>
    public MeteredStepProcessor(IProcessor inner, string stepName, IReadOnlyList<(string Name, Func<IExchange, object?> Resolver)>? tagProviders)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _stepName = stepName ?? throw new ArgumentNullException(nameof(stepName));
        _tagProviders = tagProviders;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var tags = new TagList
        {
            { "redb.route.id", exchange.RouteId },
            { "redb.route.step", _stepName }
        };
        if (_tagProviders != null)
        {
            for (int i = 0; i < _tagProviders.Count; i++)
            {
                var (name, resolver) = _tagProviders[i];
                object? value;
                try { value = resolver(exchange); }
                catch { value = null; }
                tags.Add(name, value);
            }
        }

        var sw = Stopwatch.StartNew();
        try
        {
            await _inner.Process(exchange, ct).ConfigureAwait(false);
            StepMetrics.StepProcessed.Add(1, tags);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            StepMetrics.StepFailed.Add(1, tags);
            throw;
        }
        finally
        {
            sw.Stop();
            StepMetrics.StepDuration.Record(sw.Elapsed.TotalMilliseconds, tags);
        }
    }
}
