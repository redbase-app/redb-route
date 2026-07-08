using System.Diagnostics;
using System.Transactions;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;
using redb.Route.Telemetry;
using redb.Route.Transactions;

namespace redb.Route.Tests.Processors;

/// <summary>
/// Verifies that detached branches (WireTap fire-and-forget, Debounce flush) do NOT inherit the
/// caller's ambient transaction / trace, and that WireTap does not share the transacted-action
/// dictionary with its clone. Covers the <see cref="DetachedDispatch"/>-based fix.
/// </summary>
[Collection("Telemetry")]
public class DetachedContextIsolationTests
{
    [Fact]
    public async Task WireTap_StripsTransactActionFromClone_OriginalUntouched()
    {
        IExchange? tapped = null;
        var tcs = new TaskCompletionSource();
        var tap = new DelegateProcessor((ex, _) => { tapped = ex; tcs.SetResult(); return Task.CompletedTask; });
        var processor = new WireTapProcessor(tap);

        var original = new Exchange(new Message("data"));
        original.Properties[TransactedProcessor.TransactActionPropertyKey] = new object();

        await processor.Process(original);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        tapped!.Properties.ContainsKey(TransactedProcessor.TransactActionPropertyKey).Should().BeFalse(
            "the tap clone must not share the owning TransactedProcessor's deferred-action dictionary");
        original.Properties.ContainsKey(TransactedProcessor.TransactActionPropertyKey).Should().BeTrue(
            "stripping happens on the clone only — the original is untouched");
    }

    [Fact]
    public async Task WireTap_Tap_RunsWithAmbientTransactionSuppressed()
    {
        Transaction? txInTap = null;
        var ran = false;
        var tcs = new TaskCompletionSource();
        var tap = new DelegateProcessor((ex, _) =>
        {
            txInTap = Transaction.Current;
            ran = true;
            tcs.SetResult();
            return Task.CompletedTask;
        });
        var processor = new WireTapProcessor(tap);

        using (var scope = new TransactionScope(
            TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
        {
            await processor.Process(new Exchange(new Message("data")));
            scope.Complete();
        }

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        ran.Should().BeTrue();
        txInTap.Should().BeNull("the tap must run detached from the originating transaction");
    }

    [Fact]
    public async Task WireTap_TapSpan_IsRootLinkedToOriginatingTrace()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == RouteActivitySource.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        try
        {
            Activity? tapActivity = null;
            var tcs = new TaskCompletionSource();
            var tap = new DelegateProcessor((ex, _) => { tapActivity = Activity.Current; tcs.SetResult(); return Task.CompletedTask; });
            var processor = new WireTapProcessor(tap);

            using var parent = RouteActivitySource.Source.StartActivity("test-parent");
            parent.Should().NotBeNull("listener samples the route source");
            var parentTraceId = parent!.TraceId;

            await processor.Process(new Exchange(new Message("data")));
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            tapActivity.Should().NotBeNull();
            tapActivity!.DisplayName.Should().Be("redb.route.wiretap");
            tapActivity.Parent.Should().BeNull("the tap span is re-rooted, not an implicit child of the stopped request span");
            tapActivity.TraceId.Should().NotBe(parentTraceId, "a fresh root trace is started for the detached branch");
            tapActivity.Links.Should().Contain(l => l.Context.TraceId == parentTraceId,
                "correlation to the originating trace is preserved via an ActivityLink");
        }
        finally
        {
            listener.Dispose();
        }
    }

    [Fact]
    public async Task Debounce_Flush_RunsWithAmbientTransactionSuppressed()
    {
        Transaction? txInFlush = null;
        var ran = false;
        var tcs = new TaskCompletionSource();
        var next = new DelegateProcessor((ex, _) =>
        {
            txInFlush = Transaction.Current;
            ran = true;
            tcs.SetResult();
            return Task.CompletedTask;
        });

        await using var debounce = new DebounceProcessor(next, _ => "k", TimeSpan.FromMilliseconds(50));

        using (var scope = new TransactionScope(
            TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
        {
            await debounce.Process(new Exchange(new Message("data")));
            scope.Complete();
        }

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        ran.Should().BeTrue();
        txInFlush.Should().BeNull("the debounced flush must run detached from the originating transaction");
    }
}
