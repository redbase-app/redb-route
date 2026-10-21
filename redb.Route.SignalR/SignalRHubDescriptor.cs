namespace redb.Route.SignalR;

/// <summary>
/// The hub whose container <see cref="SignalRRegistrationOptions.ConfigureHubServices"/> is filling: every consumer
/// endpoint gets a container of its own, and this says which one. A backplane keys its channels on <see cref="Path"/>,
/// so the hubs of one listener stay apart on a shared Redis or service.
/// </summary>
/// <param name="Host">Bind host of the hub's listener, as the route URI names it.</param>
/// <param name="Port">Port of the hub's listener.</param>
/// <param name="Path">The hub path, with its leading slash (<c>/chatHub</c>).</param>
public sealed record SignalRHubDescriptor(string Host, int Port, string Path);
