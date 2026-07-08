using System.Diagnostics;
using redb.Route.Core;
using redb.Route.Llm.Engine.Governance;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Providers;

namespace redb.Route.Llm.Engine.Eval;

/// <summary>
/// Drives a batch of <see cref="LlmEvalCase"/> through an <see cref="IAgentEngine"/>,
/// scores each run against the case's assertions, and (when configured) persists
/// the outcome via <see cref="IEvalRunStore"/>. Designed for both offline
/// regression suites (paired with <c>ReplayProvider</c>) and live smoke runs.
/// </summary>
public sealed class LlmEvalRunner
{
    private readonly IAgentEngine _engine;
    private readonly LlmConnectionFactory _factory;
    private readonly IEvalRunStore? _store;

    /// <summary>Creates a runner bound to a concrete engine + provider factory.</summary>
    /// <param name="engine">Engine implementation under test.</param>
    /// <param name="factory">Provider factory threaded through every <see cref="AgentRequest"/>.</param>
    /// <param name="store">Optional store for run records — null disables persistence.</param>
    public LlmEvalRunner(IAgentEngine engine, LlmConnectionFactory factory, IEvalRunStore? store = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _store = store;
    }

    /// <summary>Runs a single case to completion and returns the scored result.</summary>
    public async Task<LlmEvalResult> RunAsync(LlmEvalCase @case, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@case);

        var sw = Stopwatch.StartNew();
        AgentResponse? response = null;
        Exception? failure = null;
        try
        {
            response = await _engine.RunAsync(BuildRequest(@case), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        sw.Stop();

        var result = Score(@case, response, failure, sw.Elapsed);

        if (_store is not null)
            await _store.SaveAsync(ToRecord(@case, result), ct: ct).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// Runs every case in <paramref name="cases"/> sequentially. Failures in one
    /// case never prevent the others from running — each <see cref="LlmEvalResult"/>
    /// captures its own pass/fail and exception (if any).
    /// </summary>
    public async Task<IReadOnlyList<LlmEvalResult>> RunAllAsync(
        IEnumerable<LlmEvalCase> cases, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cases);
        var results = new List<LlmEvalResult>();
        foreach (var c in cases)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await RunAsync(c, ct).ConfigureAwait(false));
        }
        return results;
    }

    private AgentRequest BuildRequest(LlmEvalCase @case)
    {
        var content = @case.UserContent.Count > 0 ? @case.UserContent : LlmEvalCase.UserText(@case.Scenario);
        var bodyText = content.OfType<LlmTextBlock>().FirstOrDefault()?.Text ?? @case.Scenario;
        return new AgentRequest
        {
            Factory = _factory,
            Exchange = new Exchange(new Message(bodyText)),
            UserContent = content,
            SystemPrompt = @case.SystemPrompt,
            Tools = @case.Tools,
            MaxIterations = @case.MaxIterations,
            Budget = @case.Budget,
            Temperature = @case.Temperature,
            MaxTokens = @case.MaxTokens
        };
    }

    private static LlmEvalResult Score(
        LlmEvalCase @case, AgentResponse? response, Exception? failure, TimeSpan duration)
    {
        var failures = new List<string>();

        if (failure is not null)
        {
            failures.Add($"engine threw: {failure.GetType().Name}: {failure.Message}");
            return new LlmEvalResult
            {
                Case = @case,
                Response = null,
                Pass = false,
                Failures = failures,
                Duration = duration,
                Exception = failure
            };
        }

        var resp = response!;
        var text = resp.Text;

        foreach (var needle in @case.ExpectedTextContains)
            if (!text.Contains(needle, StringComparison.Ordinal))
                failures.Add($"expected text to contain '{needle}'");

        foreach (var needle in @case.ExpectedTextNotContains)
            if (text.Contains(needle, StringComparison.Ordinal))
                failures.Add($"expected text to NOT contain '{needle}'");

        if (@case.ExpectedStopReason is { } expectedStop && resp.StopReason != expectedStop)
            failures.Add($"expected stop reason {expectedStop}, got {resp.StopReason}");

        if (@case.MaxObservedIterations is { } maxIter && resp.Iterations > maxIter)
            failures.Add($"iterations {resp.Iterations} exceeded max {maxIter}");

        // Tool-call expectations are checked against the assistant turns recorded
        // in the final transcript — only the last assistant message is on
        // AgentResponse.Content, so we approximate by detecting tool-use blocks
        // there. The integration tests use FakeProvider/ReplayProvider which
        // surface tool calls explicitly enough for this check.
        if (@case.ExpectedToolCalls.Count > 0)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var b in resp.Content)
                if (b is LlmToolUseBlock use) seen.Add(use.Name);

            foreach (var name in @case.ExpectedToolCalls)
                if (!seen.Contains(name))
                    failures.Add($"expected tool '{name}' to be invoked");
        }

        var customScore = @case.CustomScorer?.Invoke(resp);
        if (customScore is { Pass: false } cs)
            failures.Add(cs.Notes is { Length: > 0 } ? $"custom scorer failed: {cs.Notes}" : "custom scorer failed");

        return new LlmEvalResult
        {
            Case = @case,
            Response = resp,
            Pass = failures.Count == 0,
            Failures = failures,
            Duration = duration,
            Score = customScore?.Score,
            Dimensions = customScore?.Dimensions,
            Notes = customScore?.Notes
        };
    }

    private EvalRunRecord ToRecord(LlmEvalCase @case, LlmEvalResult result)
    {
        var resp = result.Response;
        var iterUsage = resp is null
            ? AgentUsage.Zero
            : new AgentUsage(resp.Usage.InputTokens, resp.Usage.OutputTokens, 0m);

        var iterations = resp is null
            ? Array.Empty<EvalIterationRecord>()
            : new[]
            {
                new EvalIterationRecord
                {
                    Iteration = resp.Iterations,
                    InputTokens = resp.Usage.InputTokens,
                    OutputTokens = resp.Usage.OutputTokens,
                    DurationMs = (int)result.Duration.TotalMilliseconds,
                    Score = result.Score,
                    Notes = result.Notes
                }
            };

        return new EvalRunRecord
        {
            RunId = Guid.NewGuid().ToString("N"),
            Scenario = @case.Scenario,
            AgentFingerprint = BuildFingerprint(@case),
            Score = result.Score ?? (result.Pass ? 1.0 : 0.0),
            TotalUsage = iterUsage,
            Iterations = iterations,
            Dimensions = result.Dimensions
        };
    }

    private string BuildFingerprint(LlmEvalCase @case) =>
        $"{_factory.Provider}/{_factory.ModelId ?? "default"}#{@case.Scenario}";
}

/// <summary>Outcome of a single evaluated case.</summary>
public sealed class LlmEvalResult
{
    /// <summary>The case that produced this result.</summary>
    public required LlmEvalCase Case { get; init; }

    /// <summary>Agent response — null when the engine threw.</summary>
    public AgentResponse? Response { get; init; }

    /// <summary>True when every assertion passed.</summary>
    public required bool Pass { get; init; }

    /// <summary>Human-readable failure messages — empty when <see cref="Pass"/> is true.</summary>
    public IReadOnlyList<string> Failures { get; init; } = [];

    /// <summary>Wall-clock duration of the run.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Custom scorer's score, if any.</summary>
    public double? Score { get; init; }

    /// <summary>Custom scorer's dimensions, if any.</summary>
    public IReadOnlyDictionary<string, double>? Dimensions { get; init; }

    /// <summary>Custom scorer's free-form notes, if any.</summary>
    public string? Notes { get; init; }

    /// <summary>Engine exception, when the run threw.</summary>
    public Exception? Exception { get; init; }
}
