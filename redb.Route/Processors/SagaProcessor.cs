using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Definitions;
using redb.Route.Telemetry;

namespace redb.Route.Processors;

/// <summary>
/// Executes saga steps sequentially. On failure, runs compensations in reverse order
/// for all completed steps. Compensation failures are logged but do not interrupt rollback.
/// After all steps succeed, invokes the optional completion callback.
/// </summary>
internal sealed class SagaProcessor : IProcessor
{
    private readonly SagaStepEntry[] _steps;
    private readonly Func<IExchange, CancellationToken, Task>? _onCompletion;
    private readonly ILogger? _logger;

    public SagaProcessor(
        SagaStepEntry[] steps,
        Func<IExchange, CancellationToken, Task>? onCompletion = null,
        ILogger? logger = null)
    {
        _steps = steps;
        _onCompletion = onCompletion;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var completedCount = 0;
        var compensationFailed = false;

        try
        {
            for (var i = 0; i < _steps.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                await _steps[i].Action(exchange, ct).ConfigureAwait(false);
                completedCount++;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Saga step {StepIndex} failed, compensating {CompletedCount} step(s)", completedCount, completedCount);

            // Run compensations in reverse order for all completed steps
            for (var i = completedCount - 1; i >= 0; i--)
            {
                var compensate = _steps[i].Compensate;
                if (compensate is null) continue;

                try
                {
                    await compensate(exchange, ct).ConfigureAwait(false);
                }
                catch (Exception compEx)
                {
                    compensationFailed = true;
                    _logger?.LogError(compEx, "Saga compensation for step {StepIndex} failed", i);
                }
            }

            if (compensationFailed)
                ProcessorMetrics.SagaFailed.Add(1);
            else
                ProcessorMetrics.SagaCompensated.Add(1);

            throw;
        }

        ProcessorMetrics.SagaCompleted.Add(1);

        // All steps succeeded — invoke completion callback if present
        if (_onCompletion is not null)
            await _onCompletion(exchange, ct).ConfigureAwait(false);
    }
}
