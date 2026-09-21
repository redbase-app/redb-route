using System.Collections.Concurrent;
using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;
using redb.Route.Telemetry;

namespace redb.Route.Tests.Shared;

/// <summary>
/// Collects the route spans of one test through the OpenTelemetry SDK. A tracer provider sees every span the route
/// source emits in the process, those of test classes running in parallel included, and it records them from their
/// threads. The capture therefore opens a root span for the test, keeps only the route spans of that trace (what the
/// test itself caused), and keeps them in a thread-safe queue.
/// </summary>
/// <remarks>
/// Create it before the step that emits the span, in the test's own flow: the root becomes <see cref="Activity.Current"/>
/// for the rest of the test, so the spans the step opens are its children.
/// </remarks>
internal sealed class SpanCapture : IDisposable
{
    private static readonly ActivitySource TestSource = new("redb.Route.Tests.SpanCapture");

    private readonly ConcurrentQueue<Activity> _spans = new();
    private readonly TracerProvider _provider;
    private readonly Activity _root;

    public SpanCapture()
    {
        _provider = Sdk.CreateTracerProviderBuilder()
            .AddSource(RouteActivitySource.SourceName, TestSource.Name)
            .AddProcessor(new Collector(this))
            .Build()!;
        _root = TestSource.StartActivity("test")
            ?? throw new InvalidOperationException("The capture's own tracer provider must sample the test root span.");
    }

    /// <summary>The route spans of this test, after the provider has been flushed.</summary>
    public IReadOnlyList<Activity> Spans
    {
        get
        {
            _provider.ForceFlush(1000);
            return _spans.ToArray();
        }
    }

    public void Dispose()
    {
        _root.Dispose();
        _provider.Dispose();
    }

    private sealed class Collector(SpanCapture owner) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data)
        {
            if (data.Source.Name == RouteActivitySource.SourceName && data.TraceId == owner._root.TraceId)
                owner._spans.Enqueue(data);
        }
    }
}
