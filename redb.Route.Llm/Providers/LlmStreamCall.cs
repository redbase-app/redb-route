namespace redb.Route.Llm.Providers;

/// <summary>
/// The limits of one streamed call to the model. <see cref="LlmConnectionFactory.RequestTimeoutMs"/> runs from the
/// send to the last line, the stream included, as it does for a plain call. <see cref="LlmConnectionFactory.StreamIdleTimeoutMs"/>,
/// when set, runs only while the reader waits on the provider for a line and stops as soon as the line is in, so a
/// reader that takes its time between pieces is not taken for a silent provider. Running out throws
/// <see cref="LlmTimeoutException"/> naming the limit; a cancellation by the caller stays an
/// <see cref="OperationCanceledException"/>.
/// </summary>
internal sealed class LlmStreamCall : IDisposable
{
    private readonly LlmConnectionFactory _factory;
    private readonly string _providerId;
    private readonly CancellationToken _caller;
    private readonly TimeSpan _callLimit;
    private readonly TimeSpan? _idleLimit;
    private readonly CancellationTokenSource _call;
    private readonly CancellationTokenSource _idle;

    internal LlmStreamCall(LlmConnectionFactory factory, string providerId, CancellationToken ct)
    {
        _factory = factory;
        _providerId = providerId;
        _caller = ct;
        _callLimit = LlmHttpTransport.CallLimit(factory);
        _idleLimit = factory.StreamIdleTimeoutMs is { } idleMs ? TimeSpan.FromMilliseconds(idleMs) : null;

        _call = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _call.CancelAfter(_callLimit);
        _idle = CancellationTokenSource.CreateLinkedTokenSource(_call.Token);
    }

    /// <summary>Sends the request and opens the response within the whole-call limit.</summary>
    internal async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request)
    {
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _call.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (TimedOut(ex) is { } timeout)
        {
            throw timeout;
        }
    }

    /// <summary>The body of a successful response, within the whole-call limit.</summary>
    internal async Task<Stream> OpenAsync(HttpResponseMessage response)
    {
        try
        {
            return await response.Content.ReadAsStreamAsync(_call.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (TimedOut(ex) is { } timeout)
        {
            throw timeout;
        }
    }

    /// <summary>The next line from the provider, within both limits; null at the end of the stream.</summary>
    internal async Task<string?> ReadLineAsync(StreamReader reader)
    {
        if (_idleLimit is { } idle) _idle.CancelAfter(idle);
        try
        {
            return await reader.ReadLineAsync(_idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (TimedOut(ex) is { } timeout)
        {
            throw timeout;
        }
        finally
        {
            // The silence clock stops while the reader is busy with the line: that time is the reader's, not the
            // provider's.
            if (_idleLimit is not null && !_idle.IsCancellationRequested)
                _idle.CancelAfter(System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>The timeout a cancellation stands for, or null when the caller cancelled.</summary>
    private LlmTimeoutException? TimedOut(OperationCanceledException ex)
    {
        if (_caller.IsCancellationRequested) return null;

        var ofFactory = string.IsNullOrEmpty(_factory.Name) ? string.Empty : $" (factory '{_factory.Name}')";

        if (_call.IsCancellationRequested)
            return new LlmTimeoutException(_providerId, _factory.Name, _callLimit, LlmTimeoutKind.Call,
                $"{_providerId}: the streamed call did not finish within RequestTimeoutMs = {_callLimit.TotalMilliseconds:0} ms"
                + $"{ofFactory}. The limit covers the whole call, the stream included; a long generation needs a larger "
                + "RequestTimeoutMs.", ex);

        if (_idle.IsCancellationRequested && _idleLimit is { } idle)
            return new LlmTimeoutException(_providerId, _factory.Name, idle, LlmTimeoutKind.StreamIdle,
                $"{_providerId}: the stream sent nothing for StreamIdleTimeoutMs = {idle.TotalMilliseconds:0} ms{ofFactory}: "
                + "no text, no thinking, not even a keep-alive.", ex);

        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _idle.Dispose();
        _call.Dispose();
    }
}
