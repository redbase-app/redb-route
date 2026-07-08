using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>Tests for <see cref="WireTapProcessor"/>.</summary>
public class WireTapProcessorTests
{
    /// <summary>Tap receives a clone of the exchange.</summary>
    [Fact]
    public async Task Process_TapReceivesClone()
    {
        IExchange? tapped = null;
        var tcs = new TaskCompletionSource();

        var tap = new DelegateProcessor((ex, _) =>
        {
            tapped = ex;
            tcs.SetResult();
            return Task.CompletedTask;
        });

        var processor = new WireTapProcessor(tap);
        var original = new Exchange(new Message("data"));

        await processor.Process(original);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        tapped.Should().NotBeNull();
        tapped!.In.Body.Should().Be("data");
        tapped.Should().NotBeSameAs(original); // It's a clone
    }

    /// <summary>Original exchange is not modified by tap.</summary>
    [Fact]
    public async Task Process_OriginalUnchanged()
    {
        var tcs = new TaskCompletionSource();
        var tap = new DelegateProcessor((ex, _) =>
        {
            ex.In.Body = "modified-by-tap";
            tcs.SetResult();
            return Task.CompletedTask;
        });

        var processor = new WireTapProcessor(tap);
        var original = new Exchange(new Message("original"));

        await processor.Process(original);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        original.In.Body.Should().Be("original");
    }

    /// <summary>Null tap throws.</summary>
    [Fact]
    public void Constructor_NullTap_Throws()
    {
        var act = () => new WireTapProcessor(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>If the tap processor throws, the main pipeline completes normally.</summary>
    [Fact]
    public async Task Process_TapFailure_DoesNotAffectMainPipeline()
    {
        var tap = new DelegateProcessor((_, _) => throw new InvalidOperationException("tap boom"));
        var processor = new WireTapProcessor(tap);

        var original = new Exchange(new Message("data"));

        // Main pipeline should NOT throw even though tap throws
        var act = async () => await processor.Process(original);
        await act.Should().NotThrowAsync();

        original.In.Body.Should().Be("data");
    }

    /// <summary>OperationCanceledException in tap is silently swallowed.</summary>
    [Fact]
    public async Task Process_TapCancellation_IsSilentlySwallowed()
    {
        var tap = new DelegateProcessor((_, _) => throw new OperationCanceledException());
        var processor = new WireTapProcessor(tap);

        var act = async () => await processor.Process(new Exchange(new Message("data")));
        await act.Should().NotThrowAsync();
    }

    /// <summary>onPrepare callback is invoked on the clone before tap processing.</summary>
    [Fact]
    public async Task Process_OnPrepare_InvokedOnClone()
    {
        IExchange? tapped = null;
        var tcs = new TaskCompletionSource();

        var tap = new DelegateProcessor((ex, _) =>
        {
            tapped = ex;
            tcs.SetResult();
            return Task.CompletedTask;
        });

        var processor = new WireTapProcessor(tap, onPrepare: clone =>
        {
            clone.In.Headers["prepared"] = true;
        });

        await processor.Process(new Exchange(new Message("orig")));
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        tapped!.In.Headers["prepared"].Should().Be(true);
    }

    /// <summary>newBodyFactory replaces body on clone but original is unaffected.</summary>
    [Fact]
    public async Task Process_NewBodyFactory_ReplacesCloneBody()
    {
        IExchange? tapped = null;
        var tcs = new TaskCompletionSource();

        var tap = new DelegateProcessor((ex, _) =>
        {
            tapped = ex;
            tcs.SetResult();
            return Task.CompletedTask;
        });

        var processor = new WireTapProcessor(tap, newBodyFactory: ex => "replaced-" + ex.In.Body);

        var original = new Exchange(new Message("orig"));
        await processor.Process(original);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        tapped!.In.Body.Should().Be("replaced-orig");
        original.In.Body.Should().Be("orig");
    }
}
