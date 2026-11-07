using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using redb.Core;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Engine.Governance;
using redb.Route.Llm.Engine.Observability;
using redb.Route.Llm.Storage.Redb;
using redb.Route.Llm.Storage.Redb.Schemas;

namespace redb.Route.Tests.Llm.Storage;

/// <summary>
/// The budget and the audit observer run without an exchange, and many agent runs call them at once. Each call
/// opens a redb scope of its own and releases it, so concurrent runs never share one instance and one connection
/// (a shared connection refuses the second of two simultaneous commands).
/// </summary>
[Collection("StoragePro")]
public sealed class RedbScopePerCallTests
{
    private readonly StorageProFixture _fx;

    public RedbScopePerCallTests(StorageProFixture fx) => _fx = fx;

    /// <summary>A scope handed out by <see cref="ScopeRecordingRedb"/>: releases the real one once and remembers it did.</summary>
    public sealed class TrackedScope(RedbScope real) : IServiceScope, IAsyncDisposable
    {
        private int _released;

        public bool Released => Volatile.Read(ref _released) == 1;

        public IServiceProvider ServiceProvider => real.ServiceProvider!;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) real.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) await real.DisposeAsync();
        }
    }

    /// <summary>
    /// Stands in for the host's redb service: every call passes through to the real service, and every scope opened
    /// from it is recorded with the service it handed out.
    /// </summary>
    public class ScopeRecordingRedb : DispatchProxy
    {
        private readonly ConcurrentQueue<(IRedbService Service, TrackedScope Scope)> _opened = new();
        private IRedbService _inner = null!;

        public (IRedbService Service, TrackedScope Scope)[] Opened => _opened.ToArray();

        public static (IRedbService Proxy, ScopeRecordingRedb Recorder) Over(IRedbService inner)
        {
            var proxy = Create<IRedbService, ScopeRecordingRedb>();
            var recorder = (ScopeRecordingRedb)(object)proxy;
            recorder._inner = inner;
            return (proxy, recorder);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IRedbScopeSource.CreateScope))
            {
                var real = _inner.CreateScope();
                var tracked = new TrackedScope(real);
                _opened.Enqueue((real.Service, tracked));
                return new RedbScope(real.Service, tracked);
            }

            try
            {
                return targetMethod.Invoke(_inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }

    private (RouteContext Context, ScopeRecordingRedb Recorder) HostWithRecordingRedb()
    {
        var (proxy, recorder) = ScopeRecordingRedb.Over(_fx.Redb);
        var context = new RouteContext();
        context.AddService(typeof(IRedbService), proxy);
        return (context, recorder);
    }

    private static void EachCallOwnedItsScope((IRedbService Service, TrackedScope Scope)[] opened, int calls)
    {
        opened.Should().HaveCount(calls, "every call without an exchange opens a scope of its own");
        opened.Select(o => o.Service).Distinct().Should().HaveCount(calls, "concurrent callers never share a service");
        opened.Should().OnlyContain(o => o.Scope.Released, "each call releases the scope it opened");
    }

    [Fact]
    public async Task Budget_WithoutExchange_EachCallOpensAndReleasesItsOwnScope()
    {
        var (context, recorder) = HostWithRecordingRedb();
        var enforcer = new StoreBudgetEnforcer(new RedbCostBudgetStore(context));
        var budget = new AgentBudget(MaxInputTokens: 1_000_000, MaxOutputTokens: 1_000_000, MaxCostUsd: 0m);
        var convId = $"c-{Guid.NewGuid():N}";

        // Two runs of one conversation check the budget at the same time: readers, safe on every provider.
        await Task.WhenAll(
            enforcer.PreCheckAsync(convId, budget, AgentUsage.Zero).AsTask(),
            enforcer.PreCheckAsync(convId, budget, AgentUsage.Zero).AsTask());
        EachCallOwnedItsScope(recorder.Opened, calls: 2);

        // The write path goes through its own scope too.
        await enforcer.RecordAndCheckAsync(convId, budget, new AgentUsage(3, 4, 0m), new AgentUsage(3, 4, 0m));
        EachCallOwnedItsScope(recorder.Opened, calls: 3);
    }

    private static AgentToolInvocationContext Invocation(string convId, string toolUseId) => new()
    {
        Run = new AgentRunContext
        {
            ConversationId = convId,
            FactoryName = "test",
            ProviderId = "test",
            ModelId = "test-model",
            ExchangeId = $"x-{Guid.NewGuid():N}"
        },
        Tool = new LlmToolCapability { Name = "order_lookup", Description = "t", InputSchema = "{}" },
        InputJson = """{"orderId":"42"}""",
        OutputJson = """{"status":"shipped"}""",
        ToolUseId = toolUseId,
        Duration = TimeSpan.FromMilliseconds(5)
    };

    [Fact]
    public async Task Audit_EachInvocationOpensAndReleasesItsOwnScope()
    {
        var (context, recorder) = HostWithRecordingRedb();
        var observer = new RedbAuditObserver(context);
        var convId = $"c-{Guid.NewGuid():N}";

        // Two runs record a tool call at the same time. SQLite has one writer, and two writers racing in a test
        // deadlock on the busy handler, so there the calls run one after the other; every call still owns a scope.
        if (_fx.Provider == "sqlite")
        {
            await observer.OnToolInvokedAsync(Invocation(convId, "tu-1"));
            await observer.OnToolInvokedAsync(Invocation(convId, "tu-2"));
        }
        else
        {
            await Task.WhenAll(
                observer.OnToolInvokedAsync(Invocation(convId, "tu-1")),
                observer.OnToolInvokedAsync(Invocation(convId, "tu-2")));
        }

        EachCallOwnedItsScope(recorder.Opened, calls: 2);

        var rows = await _fx.Redb.Query<ToolAuditProps>()
            .Where(p => p.ConversationId == convId)
            .ToListAsync();
        rows.Should().HaveCount(2, "both audit rows are written through their own scopes");
    }
}
