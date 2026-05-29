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
    // End* navigation - works for any scope EIP that implements IRouteScope.
    // ---------------------------------------------------------------------
    private static IRouteDefinition EndScope(IRouteDefinition self, string scopeName)
    {
        if (self is IRouteScope scope) return scope.End();
        throw new InvalidOperationException(EndScopeMessage(scopeName, self));
    }

    private static string EndScopeMessage(string scopeName, IRouteDefinition self) => scopeName switch
    {
        "Log" => $"EndLog() called outside a Log() scope (current step: {self.GetType().Name}). Open a scope with .Log(name) first.",
        "Traced" => $"EndTraced() called outside a Traced block (current step: {self.GetType().Name}). Open a block with .Traced(name) first.",
        _ => $"End{scopeName}() called outside a {scopeName} scope (current step: {self.GetType().Name}).",
    };

    public static IRouteDefinition EndFilter(this IRouteDefinition self) => EndScope(self, "Filter");
    public static IRouteDefinition EndTraced(this IRouteDefinition self) => EndScope(self, "Traced");
    public static IRouteDefinition EndMetered(this IRouteDefinition self) => EndScope(self, "Metered");
    public static IRouteDefinition EndIdempotentConsumer(this IRouteDefinition self) => EndScope(self, "IdempotentConsumer");
    public static IRouteDefinition EndSaga(this IRouteDefinition self) => EndScope(self, "Saga");
    public static IRouteDefinition EndChoice(this IRouteDefinition self) => EndScope(self, "Choice");
    public static IRouteDefinition EndSplit(this IRouteDefinition self) => EndScope(self, "Split");
    public static IRouteDefinition EndMulticast(this IRouteDefinition self) => EndScope(self, "Multicast");
    public static IRouteDefinition EndAggregate(this IRouteDefinition self) => EndScope(self, "Aggregate");
    public static IRouteDefinition EndCircuitBreaker(this IRouteDefinition self) => EndScope(self, "CircuitBreaker");
    public static IRouteDefinition EndThrottle(this IRouteDefinition self) => EndScope(self, "Throttle");
    public static IRouteDefinition EndDebounce(this IRouteDefinition self) => EndScope(self, "Debounce");
    public static IRouteDefinition EndLoop(this IRouteDefinition self) => EndScope(self, "Loop");
    public static IRouteDefinition EndTryCatch(this IRouteDefinition self) => EndScope(self, "TryCatch");
    public static IRouteDefinition EndOnException(this IRouteDefinition self) => EndScope(self, "OnException");
    public static IRouteDefinition EndTransaction(this IRouteDefinition self) => EndScope(self, "Transaction");
    public static IRouteDefinition EndLog(this IRouteDefinition self) => EndScope(self, "Log");

    /// <summary>Universal scope close - alias for the scope-specific End*() method.</summary>
    public static IRouteDefinition End(this IRouteDefinition self) => EndScope(self, "Scope");

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
        => (self as RouteDefinition)?.Context;

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

