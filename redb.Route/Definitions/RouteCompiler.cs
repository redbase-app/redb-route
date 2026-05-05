using System.Collections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.ErrorHandling;
using redb.Route.Expressions;
using redb.Route.Processors;
using redb.Route.Processors.LoadBalancer;
using redb.Route.Serialization;
using redb.Route.Transactions;

namespace redb.Route.Definitions;

/// <summary>
/// Compiles a <see cref="RouteDefinition"/> (list of <see cref="RouteStep"/> records)
/// into a <see cref="PipelineProcessor"/> that can actually execute inside a route engine.
/// </summary>
public sealed class RouteCompiler
{
    private readonly IRouteContext _context;
    private readonly ILoggerFactory? _loggerFactory;

    /// <summary>Creates a new compiler bound to the given route context.</summary>
    /// <param name="context">Route context for resolving endpoints.</param>
    /// <param name="loggerFactory">Optional logger factory for log steps.</param>
    public RouteCompiler(IRouteContext context, ILoggerFactory? loggerFactory = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Compiles the route definition into a single <see cref="PipelineProcessor"/>.
    /// The <see cref="FromStep"/> is skipped — it is handled by the route engine when creating consumers.
    /// Route-level policies (retry, dead-letter) are applied as outermost wrappers.
    /// </summary>
    /// <param name="definition">Route definition to compile.</param>
    /// <returns>A pipeline processor representing the compiled route.</returns>
    public PipelineProcessor Compile(RouteDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var steps = definition.Steps;

        // Extract route-level policies (they are not inline processors)
        var retryStep = steps.OfType<RetryStep>().FirstOrDefault();
        var dlcStep = steps.OfType<DeadLetterChannelStep>().FirstOrDefault();
        var transactedStep = steps.OfType<TransactedStep>().FirstOrDefault();

        // Compile inline steps into a pipeline
        var corePipeline = CompileSteps(steps);

        // If no route-level policies, return the pipeline as-is
        if (retryStep is null && dlcStep is null && transactedStep is null)
            return corePipeline;

        // Apply route-level wrappers from innermost to outermost:
        //   core → transacted → retry → dead-letter
        IProcessor wrapped = corePipeline;

        if (transactedStep is not null)
        {
            var policy = transactedStep.Policy ?? TransactionPolicy.Default;
            var logger = _loggerFactory?.CreateLogger<TransactedProcessor>();
            wrapped = new TransactedProcessor(wrapped, policy, logger);
        }

        if (retryStep is not null)
        {
            var policy = new RetryPolicy
            {
                MaxRetries = retryStep.MaxRetries,
                InitialDelay = retryStep.InitialDelay ?? TimeSpan.FromMilliseconds(500),
                BackoffMultiplier = 2.0
            };
            var logger = _loggerFactory?.CreateLogger<RetryProcessor>();
            wrapped = new RetryProcessor(wrapped, policy, logger);
        }

        if (dlcStep is not null)
        {
            wrapped = new DeadLetterProcessor(wrapped, new ToProcessor(dlcStep.DeadLetterUri, _context));
        }

        var result = new PipelineProcessor();
        result.Add(wrapped);
        return result;
    }

    /// <summary>
    /// Compiles a list of steps into a pipeline processor.
    /// Wrapping steps (CircuitBreaker, Resequencer, Throttle) consume the entire tail —
    /// subsequent steps are compiled as the "next" processor they wrap.
    /// </summary>
    private PipelineProcessor CompileSteps(IReadOnlyList<RouteStep> steps)
    {
        var pipeline = new PipelineProcessor();
        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];

            // Wrapping steps — they consume ALL remaining steps as their "next" processor
            if (step is CircuitBreakerStep or ResequenceStep or ThrottleStep or ThrottleExpressionStep or KeyedThrottleStep or DebounceStep)
            {
                var tailSteps = steps.Skip(i + 1).ToList();
                IProcessor tail = tailSteps.Count > 0
                    ? CompileSteps(tailSteps)
                    : new DelegateProcessor(_ => { });

                IProcessor wrapping = step switch
                {
                    CircuitBreakerStep cb => CompileCircuitBreaker(cb, tail),
                    ResequenceStep rs => CompileResequence(rs, tail),
                    ThrottleStep th => CompileThrottle(th, tail),
                    ThrottleExpressionStep te => CompileThrottleExpression(te, tail),
                    KeyedThrottleStep kt => CompileKeyedThrottle(kt, tail),
                    DebounceStep db => new Processors.DebounceProcessor(tail, db.KeyExtractor, db.QuietPeriod, _loggerFactory?.CreateLogger<Processors.DebounceProcessor>()),
                    _ => throw new InvalidOperationException()
                };

                pipeline.Add(wrapping);
                return pipeline; // Tail already consumed
            }

            // OnException wraps the entire tail — all subsequent steps run inside try-catch
            if (step is OnExceptionStep oeStep)
            {
                var tailSteps = steps.Skip(i + 1).ToList();
                IProcessor tail = tailSteps.Count > 0
                    ? CompileSteps(tailSteps)
                    : new DelegateProcessor(_ => { });

                var oe = CompileOnException(oeStep, tail);
                pipeline.Add(oe);
                return pipeline; // Tail already consumed
            }

            // StreamCaching wraps the tail — body is cached before subsequent steps execute
            if (step is StreamCachingStep scStep)
            {
                var tailSteps = steps.Skip(i + 1).ToList();
                IProcessor tail = tailSteps.Count > 0
                    ? CompileSteps(tailSteps)
                    : new DelegateProcessor(_ => { });

                var options = new Configuration.StreamCacheOptions();
                if (scStep.SpoolThreshold.HasValue)
                    options.SpoolThreshold = scStep.SpoolThreshold.Value;

                pipeline.Add(new Processors.StreamCachingProcessor(tail, options));
                return pipeline; // Tail already consumed
            }

            var processor = CompileStep(step);
            if (processor != null)
                pipeline.Add(processor);
        }
        return pipeline;
    }

    /// <summary>
    /// Compiles a single step into a processor (or null for metadata-only steps like FromStep).
    /// </summary>
    private IProcessor? CompileStep(RouteStep step) => step switch
    {
        // Metadata / lifecycle — no processor emitted
        FromStep => null,
        TransactedStep => null, // Route-level policy — handled in Compile() as outermost wrapper

        // Processing delegates
        ProcessAsyncStep s => new DelegateProcessor(s.Action),
        ProcessSyncStep s => new DelegateProcessor(s.Action),
        ProcessInstanceStep s => s.Processor,

        // Transform / Enrich
        SetBodyStaticStep s => new DelegateProcessor(exchange => exchange.In.Body = s.Value),
        SetBodyFactoryStep s => new DelegateProcessor(exchange => exchange.In.Body = s.Factory(exchange)),
        TransformStep s => new DelegateProcessor(exchange => exchange.In.Body = s.Transform(exchange)),
        SetHeaderStaticStep s => new DelegateProcessor(exchange => exchange.In.Headers[s.Key] = s.Value),
        SetHeaderFactoryStep s => new DelegateProcessor(exchange => exchange.In.Headers[s.Key] = s.Factory(exchange)),
        RemoveHeaderStep s => new DelegateProcessor(exchange => exchange.In.Headers.Remove(s.Key)),
        SetPropertyStaticStep s => new DelegateProcessor(exchange => exchange.Properties[s.Key] = s.Value),
        SetPropertyFactoryStep s => new DelegateProcessor(exchange => exchange.Properties[s.Key] = s.Factory(exchange)),
        SetPropertyExpressionStep s => new ExpressionPropertyProcessor(s.Key, s.Expression),
        RemovePropertyStep s => new DelegateProcessor(exchange => exchange.Properties.Remove(s.Key)),
        RemoveBodyStep => new DelegateProcessor(exchange => exchange.In.Body = null),
        RethrowExceptionStep => new DelegateProcessor(exchange =>
            throw exchange.Exception ?? new InvalidOperationException("No exception on exchange to rethrow.")),
        ThrowMessageStep s => new DelegateProcessor(_ => throw new Exception(s.Message)),
        ThrowExceptionStep s => new DelegateProcessor(_ => throw s.Exception),
        ThrowExceptionTypeStep s => new DelegateProcessor(_ =>
            throw (Exception)(s.Message is not null
                ? Activator.CreateInstance(s.ExceptionType, s.Message)
                : Activator.CreateInstance(s.ExceptionType))!),

        // Transform / Enrich — expression-based
        SetBodyExpressionStep s => new ExpressionBodyProcessor(s.Expression),
        SetBodyStringExpressionStep s => new StringExpressionBodyProcessor(s.Expression),
        TransformExpressionStep s => new ExpressionBodyProcessor(s.Expression),
        TransformStringExpressionStep s => new StringExpressionBodyProcessor(s.Expression),
        SetHeaderExpressionStep s => new ExpressionHeaderProcessor(s.Name, s.Expression),
        SetHeaderStringExpressionStep s => new StringExpressionHeaderProcessor(s.Name, s.Expression),
        SetPropertyStringExpressionStep s => new StringExpressionPropertyProcessor(s.Key, s.Expression),

        // Filter
        FilterStep s => CompileFilter(s),
        FilterPredicateStep s => CompileFilterPredicate(s),
        FilterExpressionStep s => CompileFilterExpression(s),

        // Routing
        ToStep s => new ToProcessor(s.Uri, _context),
        ChoiceStep s => CompileChoice(s),
        MulticastStep s => CompileMulticast(s),
        WireTapStep s => CompileWireTap(s),

        // Split / Aggregate
        SplitStep s => CompileSplit(s),
        SplitExpressionStep s => CompileSplitExpression(s),
        StreamingSplitStep s => CompileStreamingSplit(s),
        AggregateStep s => CompileAggregate(s),

        // Loop / Delay
        LoopCountStep s => CompileLoopCount(s),
        LoopWhileStep s => CompileLoopWhile(s),
        LoopCountExpressionStep s => CompileLoopCountExpression(s),
        DelayStep s => new DelayProcessor(s.Duration),
        DelayFactoryStep s => new DelegateProcessor(async (exchange, ct) =>
        {
            var delay = s.DurationFactory(exchange);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct).ConfigureAwait(false);
        }),
        DelayExpressionStep s => new DelegateProcessor(async (exchange, ct) =>
        {
            var raw = ExpressionResolver.ProcessTemplate(s.Expression, exchange);
            var delay = ConvertToTimeSpan(raw);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct).ConfigureAwait(false);
        }),
        ThrottleExpressionStep s => throw new InvalidOperationException("ThrottleExpressionStep should be compiled by CompileSteps, not CompileStep."),

        // Error handling
        TryCatchStep s => CompileTryCatch(s),
        // OnExceptionStep is handled in CompileSteps (wraps tail) — not compiled as individual step

        // Validation
        ValidateInstanceStep s => new Validation.ValidateProcessor(s.Validator, s.ThrowOnFailure),
        ValidatePredicateStep s => new Validation.ValidateProcessor(
            new Validation.PredicateValidator(s.Predicate, s.ErrorMessage), s.ThrowOnFailure),
        ValidateJsonSchemaStringStep s => new Validation.ValidateProcessor(
            new Validation.JsonSchemaValidator(s.SchemaJson), s.ThrowOnFailure),
        ValidateJsonSchemaObjectStep s => new Validation.ValidateProcessor(
            new Validation.JsonSchemaValidator(s.Schema), s.ThrowOnFailure),
        ValidateXsdStringStep s => new Validation.ValidateProcessor(
            new Validation.XsdValidator(s.XsdContent), s.ThrowOnFailure),
        ValidateXsdNamespaceStep s => new Validation.ValidateProcessor(
            new Validation.XsdValidator(s.TargetNamespace, s.XsdContent), s.ThrowOnFailure),
        ValidateXsdSchemaSetStep s => new Validation.ValidateProcessor(
            new Validation.XsdValidator(s.SchemaSet), s.ThrowOnFailure),

        // Serialization
        MarshalStep s => CompileMarshal(s),
        UnmarshalStep s => CompileUnmarshal(s),
        ConvertBodyStep s => new Serialization.ConvertBodyProcessor(
            s.TargetType, _context.GetService<IDataFormatRegistry>()),
        StreamCachingStep => null, // handled as wrapping step in CompileSteps

        // Idempotent consumer
        IdempotentConsumerStep s => CompileIdempotentConsumer(s),
        NamedIdempotentConsumerStep s => CompileNamedIdempotentConsumer(s),

        // Claim Check
        ClaimCheckStep s => CompileClaimCheck(s),

        // Load Balancer
        LoadBalanceStep s => CompileLoadBalance(s),

        // Scatter-Gather
        ScatterGatherStep s => CompileScatterGather(s),

        // Bean / Service Activator
        BeanStep s => CompileBean(s),

        // Saga
        SagaRouteStep s => new Processors.SagaProcessor(s.Steps, s.OnCompletion, _loggerFactory?.CreateLogger<Processors.SagaProcessor>()),

        // Sampling
        SampleCountStep s => new Processors.SamplingProcessor(s.MessageFrequency),
        SamplePeriodStep s => new Processors.SamplingProcessor(s.Period),

        // Route-level policies — handled in Compile(), not as inline processors
        RetryStep => null,
        DeadLetterChannelStep => null,

        // Exchange pattern / Response
        SetPatternStep s => new DelegateProcessor(exchange => exchange.Pattern = s.Pattern),
        RespondStep s => CompileRespond(s),

        // Logging
        LogStaticStep s => CompileLog(s.Message, s.Level),
        LogDynamicStep s => CompileLogDynamic(s.MessageFactory, s.Level),
        LogTemplateStep s => CompileLogTemplate(s.Template, s.Level),
        RichLogStep s => CompileRichLog(s),

        // Stop / RollbackAll / ExceptionHandled / Imperative TX
        StopStep => new DelegateProcessor(exchange => exchange.Stop()),
        RollbackAllStep => CompileRollbackAll(),
        BeginTransactionStep s => new Transactions.BeginTransactionProcessor(
            s.Policy, _loggerFactory?.CreateLogger<Transactions.BeginTransactionProcessor>()),
        CommitTransactionStep => new Transactions.CommitTransactionProcessor(
            _loggerFactory?.CreateLogger<Transactions.CommitTransactionProcessor>()),
        RollbackTransactionStep => new Transactions.RollbackTransactionProcessor(
            _loggerFactory?.CreateLogger<Transactions.RollbackTransactionProcessor>()),
        ExceptionHandledStep => new DelegateProcessor(exchange =>
        {
            exchange.ExceptionHandled = true;
            exchange.Exception = null;
        }),

        // Throttle / CircuitBreaker / Resequencer — handled in CompileSteps() as wrapping steps
        ThrottleStep => throw new InvalidOperationException("ThrottleStep should be compiled by CompileSteps, not CompileStep."),
        CircuitBreakerStep => throw new InvalidOperationException("CircuitBreakerStep should be compiled by CompileSteps, not CompileStep."),
        ResequenceStep => throw new InvalidOperationException("ResequenceStep should be compiled by CompileSteps, not CompileStep."),
        DebounceStep => throw new InvalidOperationException("DebounceStep should be compiled by CompileSteps, not CompileStep."),
        RecipientListStep s => CompileRecipientList(s),
        EnrichStep s => CompileEnrich(s),
        PollEnrichStep s => CompilePollEnrich(s),
        DynamicRouterStep s => CompileDynamicRouter(s),

        // Traced (per-step telemetry)
        TracedStep s => CompileTraced(s),

        // Metered (per-step metrics)
        MeteredStep s => CompileMetered(s),

        _ => throw new InvalidOperationException($"Unknown route step type: {step.GetType().Name}")
    };

    // ── Compound step compilers ──

    private IProcessor CompileFilter(FilterStep step)
    {
        // When predicate returns false, stop the exchange so PipelineProcessor halts.
        return new DelegateProcessor(exchange =>
        {
            if (!step.Predicate(exchange))
                exchange.Stop();
        });
    }

    private IProcessor CompileFilterPredicate(FilterPredicateStep step)
    {
        return new DelegateProcessor(exchange =>
        {
            if (!step.Predicate.Matches(exchange))
                exchange.Stop();
        });
    }

    private IProcessor CompileFilterExpression(FilterExpressionStep step)
    {
        return new DelegateProcessor(exchange =>
        {
            var result = ExpressionResolver.ProcessTemplate(step.Expression, exchange);
            var matches = ConvertToBoolean(result);
            if (!matches)
                exchange.Stop();
        });
    }

    private static bool ConvertToBoolean(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => bool.TryParse(s, out var r) && r,
        int i => i != 0,
        long l => l != 0,
        _ => value.ToString()?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false
    };

    private ChoiceProcessor CompileChoice(ChoiceStep step)
    {
        var choice = new ChoiceProcessor();

        // Lambda-based when-clauses
        foreach (var when in step.WhenClauses)
        {
            choice.When(when.Predicate, CompileSteps(when.Steps));
        }

        // IPredicate-based when-clauses
        if (step.PredicateClauses is not null)
        {
            foreach (var when in step.PredicateClauses)
            {
                var pred = when.Predicate;
                choice.When(e => pred.Matches(e), CompileSteps(when.Steps));
            }
        }

        // String expression-based when-clauses
        if (step.ExpressionClauses is not null)
        {
            foreach (var when in step.ExpressionClauses)
            {
                var expr = when.Expression;
                choice.When(e =>
                {
                    var result = ExpressionResolver.ProcessTemplate(expr, e);
                    return ConvertToBoolean(result);
                }, CompileSteps(when.Steps));
            }
        }

        if (step.OtherwiseSteps != null)
        {
            choice.SetOtherwise(CompileSteps(step.OtherwiseSteps));
        }
        return choice;
    }

    private MulticastProcessor CompileMulticast(MulticastStep step)
    {
        var multicast = new MulticastProcessor(
            parallelProcessing: step.ParallelProcessing,
            aggregationStrategy: step.AggregationStrategy,
            stopOnException: step.StopOnException,
            timeout: step.Timeout,
            maxDegreeOfParallelism: step.MaxDegreeOfParallelism);
        foreach (var uri in step.Uris)
        {
            multicast.AddTarget(new ToProcessor(uri, _context));
        }
        return multicast;
    }

    private WireTapProcessor CompileWireTap(WireTapStep step)
    {
        return new WireTapProcessor(
            new ToProcessor(step.Uri, _context),
            step.OnPrepare,
            step.NewBodyFactory,
            _loggerFactory?.CreateLogger<WireTapProcessor>());
    }

    private SplitterProcessor CompileSplit(SplitStep step)
    {
        IProcessor target = step.SubSteps != null
            ? CompileSteps(step.SubSteps)
            : new DelegateProcessor(_ => { }); // No sub-route: just split (useful with aggregator)
        return new SplitterProcessor(
            step.Splitter,
            target,
            step.ParallelProcessing,
            step.MaxDegreeOfParallelism,
            step.AggregationStrategy,
            step.StopOnException,
            step.Timeout);
    }

    private SplitterProcessor CompileSplitExpression(SplitExpressionStep step)
    {
        IProcessor target = step.SubSteps != null
            ? CompileSteps(step.SubSteps)
            : new DelegateProcessor(_ => { });

        var expr = step.Expression;
        Func<IExchange, IEnumerable<object?>> splitter = exchange =>
        {
            var result = expr.Evaluate<object>(exchange);
            if (result is IEnumerable enumerable and not string)
                return enumerable.Cast<object?>();
            return result is not null ? [result] : [];
        };

        return new SplitterProcessor(
            splitter,
            target,
            step.ParallelProcessing,
            step.MaxDegreeOfParallelism,
            step.AggregationStrategy,
            step.StopOnException,
            step.Timeout);
    }

    private Processors.StreamingSplitterProcessor CompileStreamingSplit(StreamingSplitStep step)
    {
        IProcessor target = step.SubSteps != null
            ? CompileSteps(step.SubSteps)
            : new DelegateProcessor(_ => { });
        return new Processors.StreamingSplitterProcessor(
            step.Splitter,
            target,
            step.StopOnException);
    }

    private AggregatorProcessor CompileAggregate(AggregateStep step)
    {
        // Target is a no-op; completed aggregates land back in the pipeline
        return new AggregatorProcessor(
            step.CorrelationKey,
            step.AggregationStrategy,
            step.CompletionPredicate,
            new DelegateProcessor(_ => { }));
    }

    private LoopProcessor CompileLoopCount(LoopCountStep step)
    {
        return new LoopProcessor(CompileSteps(step.BodySteps), step.Count, step.Copy, step.ShareScope);
    }

    private LoopProcessor CompileLoopWhile(LoopWhileStep step)
    {
        return new LoopProcessor(CompileSteps(step.BodySteps), step.Condition, step.Copy, step.ShareScope);
    }

    private LoopProcessor CompileLoopCountExpression(LoopCountExpressionStep step)
    {
        var bodyProcessor = CompileSteps(step.BodySteps);
        var expr = step.Expression;
        Func<IExchange, int> countFactory = exchange =>
        {
            var raw = ExpressionResolver.ProcessTemplate(expr, exchange);
            return ConvertToInt(raw);
        };
        return new LoopProcessor(bodyProcessor, countFactory, step.Copy, step.ShareScope);
    }

    private static int ConvertToInt(object? value) => value switch
    {
        int i => i,
        long l => (int)l,
        double d => (int)d,
        string s when int.TryParse(s, out var r) => r,
        _ => throw new InvalidOperationException($"Cannot convert '{value}' to int for loop count.")
    };

    private static TimeSpan ConvertToTimeSpan(object? value) => value switch
    {
        TimeSpan ts => ts,
        int ms => TimeSpan.FromMilliseconds(ms),
        long ms => TimeSpan.FromMilliseconds(ms),
        double ms => TimeSpan.FromMilliseconds(ms),
        string s when double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var ms) => TimeSpan.FromMilliseconds(ms),
        string s when TimeSpan.TryParse(s, out var ts) => ts,
        _ => throw new InvalidOperationException($"Cannot convert '{value}' to TimeSpan for delay.")
    };

    private TryCatchProcessor CompileTryCatch(TryCatchStep step)
    {
        var tc = new TryCatchProcessor(CompileSteps(step.BodySteps),
            _loggerFactory?.CreateLogger<TryCatchProcessor>());
        foreach (var clause in step.CatchClauses)
        {
            tc.Catch(new CatchClause(clause.ExceptionType, CompileSteps(clause.HandlerSteps)));
        }
        if (step.FinallySteps != null)
        {
            tc.SetFinally(CompileSteps(step.FinallySteps));
        }
        return tc;
    }

    private OnExceptionProcessor CompileOnException(OnExceptionStep step, IProcessor body)
    {
        var oe = new OnExceptionProcessor(body);
        foreach (var handler in step.Handlers)
        {
            var pipeline = CompileSteps(handler.HandlerSteps);
            oe.Handle(handler.ExceptionType, pipeline, handler.MaxRedeliveries,
                handler.RedeliveryDelay, handler.BackoffMultiplier, handler.UseExponentialBackoff,
                handler.Handled, handler.Continued, handler.OnWhenPredicate,
                handler.RetryAttemptedLogLevel, handler.RetriesExhaustedLogLevel,
                handler.OnExceptionOccurredCallback,
                handler.RetryWhilePredicate, handler.OnRedeliveryCallback,
                handler.OnPrepareFailureCallback, handler.UseOriginalMessage,
                handler.UseOriginalBody, handler.AllowRedeliveryWhileStopping,
                handler.LogStackTrace, handler.LogExhausted);
        }
        return oe;
    }

    /// <summary>
    /// Compiles a builder-level <see cref="ExceptionRouteDefinition"/> into an
    /// <see cref="OnExceptionProcessor"/> suitable for registration as a global handler.
    /// </summary>
    internal OnExceptionProcessor CompileExceptionDefinition(ExceptionRouteDefinition definition)
    {
        // No-op body — the exception processor is the global handler itself
        var oe = new OnExceptionProcessor(new DelegateProcessor(_ => { }));
        var pipeline = CompileSteps(definition.Steps);
        oe.Handle(definition.ExceptionType, pipeline, definition.MaxRedeliveriesValue,
            definition.RedeliveryDelayValue, definition.BackoffMultiplierValue,
            definition.UseExponentialBackoffValue,
            definition.HandledValue, definition.ContinuedValue, definition.OnWhenPredicateValue,
            definition.RetryAttemptedLogLevelValue, definition.RetriesExhaustedLogLevelValue,
            definition.OnExceptionOccurredCallback,
            definition.RetryWhilePredicateValue, definition.OnRedeliveryCallbackValue,
            definition.OnPrepareFailureCallbackValue, definition.UseOriginalMessageValue,
            definition.UseOriginalBodyValue, definition.AllowRedeliveryWhileStoppingValue,
            definition.LogStackTraceValue, definition.LogExhaustedValue);
        return oe;
    }

    /// <summary>
    /// Compiles multiple builder-level exception definitions into a single
    /// <see cref="OnExceptionProcessor"/> that wraps the given body processor.
    /// Each definition registers a handler for its exception type.
    /// </summary>
    internal OnExceptionProcessor CompileExceptionDefinitions(
        IReadOnlyList<ExceptionRouteDefinition> definitions,
        IProcessor? body = null)
    {
        var oe = new OnExceptionProcessor(body ?? new DelegateProcessor(_ => { }));
        foreach (var def in definitions)
        {
            var pipeline = CompileSteps(def.Steps);
            oe.Handle(def.ExceptionType, pipeline, def.MaxRedeliveriesValue,
                def.RedeliveryDelayValue, def.BackoffMultiplierValue,
                def.UseExponentialBackoffValue,
                def.HandledValue, def.ContinuedValue, def.OnWhenPredicateValue,
                def.RetryAttemptedLogLevelValue, def.RetriesExhaustedLogLevelValue,
                def.OnExceptionOccurredCallback,
                def.RetryWhilePredicateValue, def.OnRedeliveryCallbackValue,
                def.OnPrepareFailureCallbackValue, def.UseOriginalMessageValue,
                def.UseOriginalBodyValue, def.AllowRedeliveryWhileStoppingValue,
                def.LogStackTraceValue, def.LogExhaustedValue);
        }
        return oe;
    }

    private DelegateProcessor CompileRespond(RespondStep step)
    {
        return new DelegateProcessor(exchange =>
        {
            var responseBody = step.Factory(exchange);
            exchange.Out = exchange.In.Clone();
            exchange.Out.Body = responseBody;
        });
    }

    private IProcessor CompileLog(string message, LogLevel level)
    {
        if (_loggerFactory != null)
        {
            var logger = _loggerFactory.CreateLogger("redb.Route");
            return new LogProcessor(logger, message, level);
        }
        // No logger — skip logging silently
        return new DelegateProcessor(_ => { });
    }

    private IProcessor CompileLogDynamic(Func<IExchange, string> messageFactory, LogLevel level)
    {
        if (_loggerFactory != null)
        {
            var logger = _loggerFactory.CreateLogger("redb.Route");
            return new LogProcessor(logger, messageFactory, level);
        }
        return new DelegateProcessor(_ => { });
    }

    private IProcessor CompileLogTemplate(string template, LogLevel level)
    {
        var logger = _loggerFactory?.CreateLogger("redb.Route");
        return new TemplateLogProcessor(template, level, logger);
    }

    private IProcessor CompileRichLog(RichLogStep step)
    {
        if (_loggerFactory != null)
        {
            var logger = _loggerFactory.CreateLogger("redb.Route");
            return new RichLogProcessor(logger, step.Level, step.Messages, step.MessageFuncs,
                step.HeaderNames, step.PropertyNames, step.ShowRouteId);
        }
        return new DelegateProcessor(_ => { });
    }

    private MarshalProcessor CompileMarshal(MarshalStep step)
    {
        var serializer = (IMessageSerializer)Activator.CreateInstance(step.SerializerType)!;
        return new MarshalProcessor(serializer);
    }

    private UnmarshalProcessor CompileUnmarshal(UnmarshalStep step)
    {
        var serializer = (IMessageSerializer)Activator.CreateInstance(step.SerializerType)!;
        return new UnmarshalProcessor(serializer, step.TargetType);
    }

    private IdempotentConsumerProcessor CompileIdempotentConsumer(IdempotentConsumerStep step)
    {
        var logger = _loggerFactory?.CreateLogger<IdempotentConsumerProcessor>();
        // IdempotentConsumer wraps all subsequent steps as "inner" — but since it's inline,
        // we wrap a no-op. The subsequent steps in the pipeline will follow naturally.
        // A more correct approach would be to collect all subsequent steps, but for simplicity
        // the idempotent consumer acts as a gate: it either stops the exchange (skip) or lets it through.
        return new IdempotentConsumerProcessor(
            new DelegateProcessor(_ => { }),
            step.Repository,
            step.KeyExtractor,
            step.SkipDuplicate,
            logger);
    }

    private IdempotentConsumerProcessor CompileNamedIdempotentConsumer(NamedIdempotentConsumerStep step)
    {
        var logger = _loggerFactory?.CreateLogger<IdempotentConsumerProcessor>();
        var provider = _context.GetIdempotentRepositoryProvider();
        var repository = provider.Get(step.RepositoryName);
        return new IdempotentConsumerProcessor(
            new DelegateProcessor(_ => { }),
            repository,
            step.KeyExtractor,
            step.SkipDuplicate,
            logger);
    }

    private ClaimCheckProcessor CompileClaimCheck(ClaimCheckStep step)
    {
        var logger = _loggerFactory?.CreateLogger<ClaimCheckProcessor>();
        return new ClaimCheckProcessor(step.Repository, step.Operation, step.Key, step.Ttl, logger);
    }

    private LoadBalancerProcessor CompileLoadBalance(LoadBalanceStep step)
    {
        var logger = _loggerFactory?.CreateLogger<LoadBalancerProcessor>();
        return new LoadBalancerProcessor(_context, step.Endpoints, step.Strategy, logger);
    }

    private ScatterGatherProcessor CompileScatterGather(ScatterGatherStep step)
    {
        var logger = _loggerFactory?.CreateLogger<ScatterGatherProcessor>();
        if (step.StaticRecipients != null)
            return new ScatterGatherProcessor(
                _context, step.StaticRecipients, step.AggregationStrategy,
                step.ParallelProcessing, step.MaxDegreeOfParallelism,
                step.StopOnException, step.Timeout, logger);

        if (step.DynamicRecipients == null)
            throw new InvalidOperationException("ScatterGatherStep must have either static or dynamic recipients.");

        return new ScatterGatherProcessor(
            _context, step.DynamicRecipients, step.AggregationStrategy,
            step.ParallelProcessing, step.MaxDegreeOfParallelism,
            step.StopOnException, step.Timeout, logger);
    }

    private static DelegateProcessor CompileBean(BeanStep step)
    {
        var serviceType = step.ServiceType;
        var method = step.Method;
        return new DelegateProcessor(async (exchange, ct) =>
        {
            var provider = exchange.ServiceProvider
                ?? throw new InvalidOperationException(
                    $"Bean<{serviceType.Name}> requires a ServiceProvider on the exchange. " +
                    "Ensure the route context is configured with DI.");
            var service = provider.GetRequiredService(serviceType);
            await method(service, exchange, ct).ConfigureAwait(false);
        });
    }

    // ── EIP: Throttle / CircuitBreaker / Resequencer / RecipientList / Enrich / DynamicRouter ──

    private ThrottleProcessor CompileThrottle(ThrottleStep step, IProcessor tail)
    {
        return new ThrottleProcessor(tail, step.MaxPerPeriod, step.Period,
            _loggerFactory?.CreateLogger<ThrottleProcessor>());
    }

    private IProcessor CompileThrottleExpression(ThrottleExpressionStep step, IProcessor tail)
    {
        var expr = step.Expression;
        var period = step.Period ?? TimeSpan.FromSeconds(1);
        // Evaluate the expression at runtime for each exchange.
        // We wrap with a DelegateProcessor that resolves the rate limit dynamically.
        return new DelegateProcessor(async (exchange, ct) =>
        {
            var raw = ExpressionResolver.ProcessTemplate(expr, exchange);
            var maxPerPeriod = ConvertToInt(raw);
            if (maxPerPeriod <= 0) maxPerPeriod = 1;
            // Create a throttle for this exchange (simplified: no shared state across exchanges)
            using var throttle = new ThrottleProcessor(tail, maxPerPeriod, period);
            await throttle.Process(exchange, ct).ConfigureAwait(false);
        });
    }

    private KeyedThrottleProcessor CompileKeyedThrottle(KeyedThrottleStep step, IProcessor tail)
    {
        return new KeyedThrottleProcessor(tail, step.KeyExtractor, step.MaxPerPeriod, step.Period,
            _loggerFactory?.CreateLogger<KeyedThrottleProcessor>());
    }

    private CircuitBreakerProcessor CompileCircuitBreaker(CircuitBreakerStep step, IProcessor tail)
    {
        IProcessor? fallback = step.FallbackSteps is { Count: > 0 }
            ? CompileSteps(step.FallbackSteps)
            : null;

        return new CircuitBreakerProcessor(
            tail,
            step.FailureThreshold,
            step.ResetTimeout,
            step.HalfOpenMaxCalls,
            fallback,
            _loggerFactory?.CreateLogger<CircuitBreakerProcessor>());
    }

    private ResequencerProcessor CompileResequence(ResequenceStep step, IProcessor tail)
    {
        return new ResequencerProcessor(tail, step.KeySelector, step.BatchSize, step.Timeout);
    }

    private RecipientListProcessor CompileRecipientList(RecipientListStep step)
    {
        return new RecipientListProcessor(
            _context,
            step.RecipientListFactory,
            step.ParallelProcessing,
            step.StopOnException,
            step.AggregationStrategy);
    }

    private EnrichProcessor CompileEnrich(EnrichStep step)
    {
        return new EnrichProcessor(_context, step.ResourceUri, step.MergeStrategy);
    }

    private PollEnrichProcessor CompilePollEnrich(PollEnrichStep step)
    {
        return new PollEnrichProcessor(_context, step.ResourceUri, step.MergeStrategy, step.Timeout);
    }

    private DynamicRouterProcessor CompileDynamicRouter(DynamicRouterStep step)
    {
        return new DynamicRouterProcessor(_context, step.RoutingFunction,
            _loggerFactory?.CreateLogger<DynamicRouterProcessor>());
    }

    private IProcessor CompileTraced(TracedStep step)
    {
        var inner = CompileSteps(step.SubSteps);
        var spanName = step.SpanName;

        // If span name contains ${...} expressions, resolve at runtime
        if (spanName.Contains("${"))
        {
            return new DelegateProcessor(async (exchange, ct) =>
            {
                var resolvedName = Expressions.ExpressionResolver.ProcessTemplate(spanName, exchange);
                using var activity = Telemetry.RouteActivitySource.Source.StartActivity(
                    resolvedName, System.Diagnostics.ActivityKind.Internal);
                await inner.Process(exchange, ct).ConfigureAwait(false);
            });
        }

        // Static span name — use InstrumentedProcessor directly
        return new Telemetry.InstrumentedProcessor(inner, spanName);
    }

    private IProcessor CompileMetered(MeteredStep step)
    {
        var inner = CompileSteps(step.SubSteps);
        return new Telemetry.MeteredStepProcessor(inner, step.StepName);
    }

    private static DelegateProcessor CompileRollbackAll()
    {
        return new DelegateProcessor(async (exchange, ct) =>
        {
            if (exchange.Properties.TryGetValue(Transactions.TransactedProcessor.TransactActionPropertyKey, out var raw) &&
                raw is System.Collections.Concurrent.ConcurrentDictionary<string, Abstractions.ITransactedAction> actions)
            {
                foreach (var kvp in actions)
                    await kvp.Value.Rollback(ct).ConfigureAwait(false);
                actions.Clear();
            }
            exchange.Properties["RollbackOnly"] = true;
        });
    }
}
