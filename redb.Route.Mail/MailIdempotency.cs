using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Mail;

/// <summary>
/// The idempotency of a mail consumer, as Apache Camel's mail consumer keeps it: a message's key is added to the
/// repository before its route and the message is skipped when the repository already holds it; a failed exchange
/// removes the key, so the message is routed again; a successful one confirms it, or removes it when
/// <see cref="MailEndpointOptions.IdempotentRepositoryRemoveOnCommit"/> says so.
/// </summary>
internal sealed class MailIdempotency
{
    private readonly IIdempotentRepository _repository;
    private readonly bool _removeOnCommit;

    private MailIdempotency(IIdempotentRepository repository, bool removeOnCommit)
    {
        _repository = repository;
        _removeOnCommit = removeOnCommit;
    }

    /// <summary>
    /// The idempotency the options ask for: the registered <see cref="MailEndpointOptions.IdempotentRepository"/>,
    /// else the consumer's own in-memory set for <see cref="MailEndpointOptions.Idempotent"/>, else none.
    /// </summary>
    public static MailIdempotency? Create(MailEndpointOptions options, IEndpoint endpoint)
    {
        if (!string.IsNullOrEmpty(options.IdempotentRepository))
        {
            var context = (endpoint.Component as ComponentBase)?.Context
                ?? throw new InvalidOperationException(
                    $"idempotentRepository '{options.IdempotentRepository}' needs a route context: register the " +
                    "repository with context.AddIdempotentRepository(name, repository).");
            return new MailIdempotency(
                context.GetIdempotentRepositoryProvider().Get(options.IdempotentRepository),
                options.IdempotentRepositoryRemoveOnCommit);
        }

        // idempotent=true: the messages this consumer routed successfully are skipped until it restarts.
        return options.Idempotent ? new MailIdempotency(new InMemoryIdempotentRepository(), removeOnCommit: false) : null;
    }

    /// <summary>Adds the key before the route; <c>false</c> when the repository already holds it (a duplicate).</summary>
    public Task<bool> TryAdd(string key, CancellationToken ct) => _repository.Add(key, ct);

    /// <summary>The exchange succeeded and the message is committed.</summary>
    public Task Commit(string key, CancellationToken ct) =>
        _removeOnCommit ? _repository.Remove(key, ct) : _repository.Confirm(key, ct);

    /// <summary>The exchange failed and the message is left as it is: it may be routed again.</summary>
    public Task Rollback(string key, CancellationToken ct) => _repository.Remove(key, ct);
}
