using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Definitions;
using redb.Route.Expressions;
using redb.Route.Transactions;

namespace redb.Route.Abstractions;

/// <summary>
/// Convenience alias and End* navigation extension methods on <see cref="IRouteDefinition"/>.
/// They map alternative names that tests and v1 callers expect onto the canonical v2 DSL surface.
/// </summary>
public static class RouteDefinitionAliasExtensions
{
    // ---------------------------------------------------------------------
    // End* navigation - walks the Parent chain looking for a scope of the
    // requested type and closes everything up to and including it. This means
    // .EndChoice() called from inside a Log scope inside a When closes the Log
    // and the When and the Choice in one shot, returning the parent route.
    // ---------------------------------------------------------------------
    private static IRouteDefinition CloseToScope<TScope>(IRouteDefinition self, string scopeName)
        where TScope : class, IProcessorDefinition
    {
        IProcessorDefinition? cur = self as IProcessorDefinition;
        while (cur is not null)
        {
            if (cur is TScope match)
            {
                var parent = match.Parent
                    ?? throw new InvalidOperationException(
                        $"End{scopeName}() found a {typeof(TScope).Name} with no parent — this scope is not attached to a route.");
                if (parent is IRouteDefinition rd) return rd;
                throw new InvalidOperationException(
                    $"End{scopeName}() resolved a parent of type {parent.GetType().Name} which does not implement IRouteDefinition.");
            }
            cur = cur.Parent;
        }
        throw new InvalidOperationException(
            $"End{scopeName}() called outside a {scopeName} scope (current step: {self.GetType().Name}). " +
            $"Open a {scopeName} scope first.");
    }

    /// <summary>Closes the nearest enclosing Filter scope.</summary>
    public static IRouteDefinition EndFilter(this IRouteDefinition self) => CloseToScope<FilterDefinition>(self, "Filter");
    /// <summary>Closes the nearest enclosing Traced block.</summary>
    public static IRouteDefinition EndTraced(this IRouteDefinition self) => CloseToScope<TracedDefinition>(self, "Traced");
    /// <summary>Closes the nearest enclosing Metered block.</summary>
    public static IRouteDefinition EndMetered(this IRouteDefinition self) => CloseToScope<MeteredDefinition>(self, "Metered");
    /// <summary>Closes the nearest enclosing IdempotentConsumer scope.</summary>
    public static IRouteDefinition EndIdempotentConsumer(this IRouteDefinition self) => CloseToScope<IdempotentConsumerDefinition>(self, "IdempotentConsumer");
    /// <summary>Closes the nearest enclosing Saga scope.</summary>
    public static IRouteDefinition EndSaga(this IRouteDefinition self) => CloseToScope<SagaDefinition>(self, "Saga");
    /// <summary>Closes the nearest enclosing Choice scope (and any open When/Otherwise inside it).</summary>
    public static IRouteDefinition EndChoice(this IRouteDefinition self) => CloseToScope<ChoiceDefinition>(self, "Choice");
    /// <summary>Closes the nearest enclosing When branch and returns its parent Choice (typed as IRouteDefinition).</summary>
    public static IRouteDefinition EndWhen(this IRouteDefinition self) => CloseToScope<WhenDefinition>(self, "When");
    /// <summary>Closes the nearest enclosing Otherwise branch and returns its parent Choice (typed as IRouteDefinition).</summary>
    public static IRouteDefinition EndOtherwise(this IRouteDefinition self) => CloseToScope<OtherwiseDefinition>(self, "Otherwise");
    /// <summary>Closes the nearest enclosing Split scope.</summary>
    public static IRouteDefinition EndSplit(this IRouteDefinition self) => CloseToScope<SplitDefinition>(self, "Split");
    /// <summary>Closes the nearest enclosing Multicast scope.</summary>
    public static IRouteDefinition EndMulticast(this IRouteDefinition self) => CloseToScope<MulticastDefinition>(self, "Multicast");
    /// <summary>Closes the nearest enclosing Aggregate scope.</summary>
    public static IRouteDefinition EndAggregate(this IRouteDefinition self) => CloseToScope<AggregateDefinition>(self, "Aggregate");
    /// <summary>Closes the nearest enclosing CircuitBreaker scope.</summary>
    public static IRouteDefinition EndCircuitBreaker(this IRouteDefinition self) => CloseToScope<CircuitBreakerDefinition>(self, "CircuitBreaker");
    /// <summary>Closes the nearest enclosing Throttle scope.</summary>
    public static IRouteDefinition EndThrottle(this IRouteDefinition self) => CloseToScope<ThrottleDefinition>(self, "Throttle");
    /// <summary>Closes the nearest enclosing Debounce scope.</summary>
    public static IRouteDefinition EndDebounce(this IRouteDefinition self) => CloseToScope<DebounceDefinition>(self, "Debounce");
    /// <summary>Closes the nearest enclosing Loop scope.</summary>
    public static IRouteDefinition EndLoop(this IRouteDefinition self) => CloseToScope<LoopDefinition>(self, "Loop");
    /// <summary>Closes the nearest enclosing TryCatch scope (and any open Catch/Finally inside it).</summary>
    public static IRouteDefinition EndTryCatch(this IRouteDefinition self) => CloseToScope<TryCatchDefinition>(self, "TryCatch");
    /// <summary>Closes the nearest enclosing OnException scope.</summary>
    public static IRouteDefinition EndOnException(this IRouteDefinition self) => CloseToScope<OnExceptionDefinition>(self, "OnException");
    /// <summary>Closes the nearest enclosing Transaction scope.</summary>
    public static IRouteDefinition EndTransaction(this IRouteDefinition self) => CloseToScope<TransactionDefinition>(self, "Transaction");
    /// <summary>Closes the nearest enclosing Log (rich) scope.</summary>
    public static IRouteDefinition EndLog(this IRouteDefinition self) => CloseToScope<RichLogScopeDefinition>(self, "Log");
    /// <summary>Closes the nearest enclosing Resequence scope.</summary>
    public static IRouteDefinition EndResequence(this IRouteDefinition self) => CloseToScope<ResequenceDefinition>(self, "Resequence");

    /// <summary>
    /// Universal scope close — walks up to the nearest enclosing <see cref="IRouteScope"/>
    /// and closes it. Equivalent to calling the matching End*() method.
    /// </summary>
    public static IRouteDefinition End(this IRouteDefinition self)
    {
        IProcessorDefinition? cur = self as IProcessorDefinition;
        while (cur is not null)
        {
            if (cur is IRouteScope scope) return scope.End();
            cur = cur.Parent;
        }
        throw new InvalidOperationException(
            $"End() called outside any open scope (current step: {self.GetType().Name}).");
    }

    // ---------------------------------------------------------------------
    // Sibling-branch openers — When() / Otherwise() reachable from anywhere
    // inside an open Choice scope, even after a sub-scope was just closed
    // (e.g. .EndSplit().When(...) — typed as IRouteDefinition but the real
    // current node is a WhenDefinition whose Parent is a ChoiceDefinition).
    // ---------------------------------------------------------------------
    private static ChoiceDefinition FindEnclosingChoice(IRouteDefinition self, string method)
    {
        IProcessorDefinition? cur = self as IProcessorDefinition;
        while (cur is not null)
        {
            if (cur is ChoiceDefinition choice) return choice;
            cur = cur.Parent;
        }
        throw new InvalidOperationException(
            $"{method}() called outside a Choice scope (current step: {self.GetType().Name}). Open a Choice scope first.");
    }

    /// <summary>Opens (or continues) a When branch on the nearest enclosing Choice scope.</summary>
    public static WhenDefinition When(this IRouteDefinition self, Func<IExchange, bool> predicate)
        => FindEnclosingChoice(self, "When").When(predicate);

    /// <summary>Opens (or continues) a When branch on the nearest enclosing Choice scope.</summary>
    public static WhenDefinition When(this IRouteDefinition self, IExpression expression)
        => FindEnclosingChoice(self, "When").When(expression);

    /// <summary>Opens (or continues) a When branch on the nearest enclosing Choice scope using a Simple template.</summary>
    public static WhenDefinition When(this IRouteDefinition self, string simpleTemplate)
        => FindEnclosingChoice(self, "When").When(new redb.Route.Expressions.StringExpression(simpleTemplate));

    /// <summary>Opens the Otherwise branch on the nearest enclosing Choice scope.</summary>
    public static OtherwiseDefinition Otherwise(this IRouteDefinition self)
        => FindEnclosingChoice(self, "Otherwise").Otherwise();

    // ---------------------------------------------------------------------
    // Transaction alias (Transacted in v1 / Camel).
    // ---------------------------------------------------------------------
    public static TransactionDefinition Transacted(this IRouteDefinition self) => self.Transaction();
    public static TransactionDefinition Transacted(this IRouteDefinition self, TransactionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return self.Transaction(policy);
    }
    public static TransactionDefinition Transacted(this IRouteDefinition self, string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        return self.Transaction(TransactionPolicy.FromName(policyName));
    }

    // ---------------------------------------------------------------------
    // Expression aliases - map *Expression(IExpression) to canonical setters.
    // ---------------------------------------------------------------------
    public static IRouteDefinition SetBodyExpression(this IRouteDefinition self, IExpression expression)
        => self.SetBody(expression);

    public static IRouteDefinition SetHeaderExpression(this IRouteDefinition self, string name, IExpression expression)
        => self.SetHeader(name, expression);

    public static IRouteDefinition SetPropertyExpression(this IRouteDefinition self, string name, IExpression expression)
        => self.SetProperty(name, expression);

    public static IRouteDefinition TransformExpression(this IRouteDefinition self, IExpression expression)
        => self.Transform(expression);

    public static LoopDefinition LoopExpression(this IRouteDefinition self, IExpression expression)
        => self.Loop(ex => expression.Evaluate<int>(ex));

    public static IRouteDefinition DelayExpression(this IRouteDefinition self, IExpression expression)
        => self.Delay(ex => TimeSpan.FromMilliseconds(expression.Evaluate<long>(ex)));

    public static IRouteDefinition DelayExpression(this IRouteDefinition self, string simpleTemplate)
    {
        var expr = new redb.Route.Expressions.StringExpression(simpleTemplate);
        return self.Delay(ex => TimeSpan.FromMilliseconds(expr.Evaluate<long>(ex)));
    }

    // ---------------------------------------------------------------------
    // TryCatch aliases (Camel-style doTry / doCatch / doFinally).
    // ---------------------------------------------------------------------
    public static TryCatchDefinition DoTry(this IRouteDefinition self) => self.TryCatch();

    public static CatchDefinition DoCatch<TException>(this IRouteDefinition self) where TException : Exception
    {
        return self switch
        {
            TryCatchDefinition tc => tc.Catch(typeof(TException)),
            CatchDefinition cd => cd.EndCatch().Catch(typeof(TException)),
            FinallyDefinition => throw new InvalidOperationException("DoCatch() cannot follow Finally() — catches must precede the finally block."),
            _ => throw new InvalidOperationException($"DoCatch() requires the previous step to be DoTry or DoCatch (got {self.GetType().Name})."),
        };
    }

    public static FinallyDefinition DoFinally(this IRouteDefinition self)
    {
        return self switch
        {
            TryCatchDefinition tc => tc.Finally(),
            CatchDefinition cd => cd.Finally(),
            _ => throw new InvalidOperationException($"DoFinally() requires the previous step to be DoTry or DoCatch (got {self.GetType().Name})."),
        };
    }

    // ---------------------------------------------------------------------
    // Context accessor.
    // ---------------------------------------------------------------------
    public static IRouteContext? GetContext(this IRouteDefinition self)
    {
        // Walk the Parent chain to reach the root RouteDefinition (every scope
        // class — WhenDefinition, LoopDefinition, TracedDefinition, etc. —
        // inherits from RouteDefinitionBase<TSelf>, not from RouteDefinition,
        // so a direct `self as RouteDefinition` cast only matches the root).
        IProcessorDefinition? cur = self as IProcessorDefinition;
        while (cur is not null)
        {
            if (cur is RouteDefinition rd) return rd.Context;
            cur = cur.Parent;
        }
        return null;
    }

    // ---------------------------------------------------------------------
    // RouteDefinition-level aliases.
    // ---------------------------------------------------------------------
    public static IRouteDefinition ShowRouteId(this IRouteDefinition self, bool value = true)
    {
        throw new InvalidOperationException(
            $"ShowRouteId() called outside a Log() scope (current step: {self.GetType().Name}). Open a scope with .Log(name) first.");
    }

    // ---------------------------------------------------------------------
    // OnException-scope aliases (work when previous step is OnExceptionDefinition).
    // ---------------------------------------------------------------------
    private static T RequireScope<T>(IRouteDefinition self, string method) where T : class
    {
        if (self is T t) return t;
        throw new InvalidOperationException(
            $"{method}() must be called on a {typeof(T).Name} scope (got {self.GetType().Name}).");
    }

    public static IRouteDefinition MaximumRedeliveries(this IRouteDefinition self, int count)
        => RequireScope<OnExceptionDefinition>(self, "MaximumRedeliveries").MaximumRedeliveries(count);

    public static IRouteDefinition Handled(this IRouteDefinition self, bool value = true)
        => RequireScope<OnExceptionDefinition>(self, "Handled").Handled(value);

    public static IRouteDefinition Continued(this IRouteDefinition self, bool value = true)
        => RequireScope<OnExceptionDefinition>(self, "Continued").Continued(value);

    public static IRouteDefinition RedeliveryPolicy(this IRouteDefinition self, RedeliveryPolicy policy)
        => RequireScope<OnExceptionDefinition>(self, "RedeliveryPolicy").RedeliveryPolicy(policy);

    public static IRouteDefinition OnWhen(this IRouteDefinition self, Func<IExchange, bool> predicate)
        => RequireScope<OnExceptionDefinition>(self, "OnWhen").OnWhen(predicate);

    public static IRouteDefinition OnRedelivery(this IRouteDefinition self, Action<IExchange> action)
        => RequireScope<OnExceptionDefinition>(self, "OnRedelivery").OnRedelivery(action);

    public static IRouteDefinition OnExceptionOccurred(this IRouteDefinition self, Action<IExchange> action)
        => RequireScope<OnExceptionDefinition>(self, "OnExceptionOccurred").OnExceptionOccurred(action);

    public static IRouteDefinition RetryAttemptedLogLevel(this IRouteDefinition self, LogLevel level)
        => RequireScope<OnExceptionDefinition>(self, "RetryAttemptedLogLevel").RetryAttemptedLogLevel(level);

    public static IRouteDefinition RetriesExhaustedLogLevel(this IRouteDefinition self, LogLevel level)
        => RequireScope<OnExceptionDefinition>(self, "RetriesExhaustedLogLevel").RetriesExhaustedLogLevel(level);

    public static IRouteDefinition BackOffMultiplier(this IRouteDefinition self, double multiplier)
        => RequireScope<OnExceptionDefinition>(self, "BackOffMultiplier").BackOffMultiplier(multiplier);

    public static IRouteDefinition UseExponentialBackOff(this IRouteDefinition self, bool value = true)
        => RequireScope<OnExceptionDefinition>(self, "UseExponentialBackOff").UseExponentialBackOff(value);

    // ---------------------------------------------------------------------
    // Saga-scope aliases.
    // ---------------------------------------------------------------------
    public static IRouteDefinition SagaStep(this IRouteDefinition self, Action<IExchange> action, Action<IExchange> compensate)
        => RequireScope<SagaDefinition>(self, "SagaStep").SagaStep(action, compensate);

    public static IRouteDefinition SagaStep(this IRouteDefinition self, Action<IExchange> action)
        => RequireScope<SagaDefinition>(self, "SagaStep").SagaStep(action);

    public static IRouteDefinition OnSagaCompletion(this IRouteDefinition self, Action<IExchange> callback)
        => RequireScope<SagaDefinition>(self, "OnSagaCompletion").OnSagaCompletion(callback);
}

