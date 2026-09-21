namespace redb.Route.Abstractions;

/// <summary>
/// Marks a message body that reads from resources its exchange holds (registered with
/// <see cref="ExchangeResources.ReleaseWithExchange"/>), such as a streamed query result, and so cannot be read once the
/// exchange has ended.
/// <para>
/// A caller that ends the exchange before it hands the body on refuses such a body instead of returning one that is
/// already dead: <see cref="IProducerTemplate.RequestBody(IEndpoint, object, CancellationToken)"/> does. Read it through
/// <see cref="IProducerTemplate.RequestAsync(IEndpoint, IExchange, CancellationToken)"/> and dispose the exchange after
/// the body has been read.
/// </para>
/// </summary>
public interface IExchangeBoundBody;
