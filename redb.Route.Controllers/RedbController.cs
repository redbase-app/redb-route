using redb.Route.Abstractions;

namespace redb.Route.Controllers;

/// <summary>
/// Base class for transport-agnostic controllers dispatched via <see cref="ControllerDispatcherProcessor"/>.
/// Controllers sit behind direct: endpoints and are unaware of transport — any InOut endpoint can invoke them.
/// </summary>
public abstract class RedbController
{
    /// <summary>Route context this controller operates within.</summary>
    public IRouteContext Context { get; internal set; } = null!;

    /// <summary>Current exchange being processed.</summary>
    public IExchange Exchange { get; internal set; } = null!;
}
