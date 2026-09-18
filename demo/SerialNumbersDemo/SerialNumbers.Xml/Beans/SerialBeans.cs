using Microsoft.Extensions.DependencyInjection;
using redb.Core;
using redb.Route.Abstractions;
using SerialNumbers.Core.Services;

namespace SerialNumbers.Xml.Beans;

/// <summary>
/// The bridge between the markup and the module's services. A route calls
/// <c>bean:#name?method=X</c>, and the bean invoker accepts <c>(IExchange)</c> or
/// <c>(IExchange, CancellationToken)</c> - so every method here is that shape and hands the
/// exchange to the service unchanged. The redb instance is the exchange's own, the same one
/// <c>ProcessWithRedb</c> resolves in the C# spelling: one connection per exchange, enlisted in
/// the transaction the route opened.
/// </summary>
public sealed class SerialRequestBeans
{
    public Task Register(IExchange exchange, CancellationToken ct)
        => SerialRequestService.RegisterAsync(Redb(exchange), exchange, ct);

    public Task Allocate(IExchange exchange, CancellationToken ct)
        => SerialRequestService.AllocateAsync(Redb(exchange), exchange, ct);

    public Task Reject(IExchange exchange, CancellationToken ct)
        => SerialRequestService.RejectAsync(Redb(exchange), exchange, ct);

    public Task QueueResponse(IExchange exchange, CancellationToken ct)
        => SerialRequestService.QueueResponseAsync(Redb(exchange), exchange, ct);

    internal static IRedbService Redb(IExchange exchange)
        => exchange.ServiceProvider?.GetService<IRedbService>()
           ?? throw new InvalidOperationException(
               "The exchange carries no IRedbService: the module must run in a host that registers redb (the Tsak worker does).");
}

/// <summary>What the intake records when a file cannot be processed.</summary>
public sealed class IntakeBeans
{
    public Task RecordInvalid(IExchange exchange, CancellationToken ct)
        => IntakeRecorder.RecordInvalidAsync(SerialRequestBeans.Redb(exchange), exchange, ct);

    public Task RecordParked(IExchange exchange, CancellationToken ct)
        => IntakeRecorder.RecordParkedAsync(SerialRequestBeans.Redb(exchange), exchange, ct);
}
