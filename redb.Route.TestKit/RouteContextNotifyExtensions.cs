using redb.Route.Abstractions;

namespace redb.Route.TestKit;

/// <summary>Entry point for <see cref="NotifyBuilder"/>: <c>ctx.Notify().FromRoute("orders").WhenDone(3).Create()</c>.</summary>
public static class RouteContextNotifyExtensions
{
    /// <summary>Starts building a <see cref="NotifyMatcher"/> for this context.</summary>
    public static NotifyBuilder Notify(this IRouteContext context) => new(context);
}
