using Amazon.SQS;
using redb.Route.Abstractions;

namespace redb.Route.Sqs;

/// <summary>
/// Deferred SQS acknowledgement enrolled as an <see cref="ITransactedAction"/> when the endpoint is
/// <c>transacted=true</c>. Commit deletes the message (or is a no-op when <c>deleteAfterRead=false</c>);
/// rollback resets its visibility to 0 so the broker redelivers it immediately. The surrounding
/// <c>TransactedProcessor</c> drives commit/rollback together with any redb DB work.
/// </summary>
internal sealed class SqsAckAction : ITransactedAction
{
    private readonly IAmazonSQS _client;
    private readonly string _queueUrl;
    private readonly string _receiptHandle;
    private readonly bool _deleteAfterRead;
    private int _settled;

    public SqsAckAction(IAmazonSQS client, string queueUrl, string receiptHandle, bool deleteAfterRead)
    {
        _client = client;
        _queueUrl = queueUrl;
        _receiptHandle = receiptHandle;
        _deleteAfterRead = deleteAfterRead;
    }

    /// <summary>True once commit or rollback has run — guards against a double settle.</summary>
    public bool Settled => Volatile.Read(ref _settled) == 1;

    /// <inheritdoc />
    public async Task Commit(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _settled, 1, 0) != 0) return;
        if (_deleteAfterRead)
            await _client.DeleteMessageAsync(_queueUrl, _receiptHandle, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task Rollback(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _settled, 1, 0) != 0) return;
        await _client.ChangeMessageVisibilityAsync(_queueUrl, _receiptHandle, 0, ct).ConfigureAwait(false);
    }
}
