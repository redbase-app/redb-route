using System.Diagnostics;
using redb.Route.Telemetry;

namespace redb.Route.Tests.Http;

/// <summary>
/// Collects the spans of <b>one test</b> from the process-wide route <see cref="ActivitySource"/>.
/// <para>
/// An <see cref="ActivityListener"/> is process state: filtering by source name alone (the obvious
/// spelling, and the one used until 2026-09-01) makes a test see the spans of every route any other
/// test is running at that moment. That is why the telemetry tests flaked under a full parallel run
/// and were always green in isolation — six clean runs of the untouched tree produced two failures.
/// Serialising the tests by collection cannot help: the listener is not per collection.
/// </para>
/// <para>
/// So the probe keeps the source filter (there is only one source) and adds the real discriminator:
/// a predicate over the finished activity, normally its <c>redb.route.id</c> tag. Spans of other
/// tests are seen and dropped.
/// </para>
/// </summary>
internal sealed class RouteTelemetryProbe : IDisposable
{
    /// <summary>Tag every route span carries: the id of the route the exchange was travelling.</summary>
    public const string RouteIdTag = "redb.route.id";

    /// <summary>Tag a transport span carries: the (sanitized) endpoint URI.</summary>
    public const string EndpointTag = "redb.route.endpoint";

    private readonly ActivityListener _listener;
    private readonly List<Activity> _collected = [];
    // A plain object, not System.Threading.Lock: the test projects also target net8.0, where that type does not exist.
    private readonly object _sync = new();
    private readonly Func<Activity, bool> _isMine;

    /// <summary>Collects the finished spans <paramref name="isMine"/> accepts.</summary>
    public RouteTelemetryProbe(Func<Activity, bool> isMine)
    {
        _isMine = isMine ?? throw new ArgumentNullException(nameof(isMine));
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RouteActivitySource.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = Collect,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>Collects spans whose <c>redb.route.id</c> begins with <paramref name="routeIdPrefix"/>.</summary>
    public static RouteTelemetryProbe ForRouteIdPrefix(string routeIdPrefix)
        => new(activity => Tag(activity, RouteIdTag)?.StartsWith(routeIdPrefix, StringComparison.Ordinal) == true);

    /// <summary>Collects spans whose <c>redb.route.endpoint</c> contains <paramref name="fragment"/> (a port, a path).</summary>
    public static RouteTelemetryProbe ForEndpointContaining(string fragment)
        => new(activity => Tag(activity, EndpointTag)?.Contains(fragment, StringComparison.Ordinal) == true);

    /// <summary>The spans collected so far, oldest first.</summary>
    public IReadOnlyList<Activity> Activities
    {
        get { lock (_sync) return [.. _collected]; }
    }

    /// <summary>Value of <paramref name="tag"/> on <paramref name="activity"/>, or <c>null</c>.</summary>
    public static string? Tag(Activity activity, string tag) => activity.GetTagItem(tag)?.ToString();

    /// <inheritdoc />
    public void Dispose() => _listener.Dispose();

    // ActivityStopped fires on whatever thread finished the span, and several routes may finish at once.
    private void Collect(Activity activity)
    {
        if (!_isMine(activity)) return;
        lock (_sync) _collected.Add(activity);
    }
}
