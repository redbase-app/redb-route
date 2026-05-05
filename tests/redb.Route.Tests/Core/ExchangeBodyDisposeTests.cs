using redb.Route.Core;
using FluentAssertions;

namespace redb.Route.Tests.Core;

public class ExchangeBodyDisposeTests
{
    [Fact]
    public async Task DisposeAsync_StreamBody_DisposesStream()
    {
        var stream = new MemoryStream(new byte[] { 1, 2, 3 });
        var exchange = new Exchange(new Message(stream));

        await exchange.DisposeAsync().ConfigureAwait(false);

        stream.CanRead.Should().BeFalse();
    }

    [Fact]
    public async Task DisposeAsync_StringBody_NoError()
    {
        var exchange = new Exchange(new Message("hello"));

        var act = async () => await exchange.DisposeAsync().ConfigureAwait(false);

        await act.Should().NotThrowAsync().ConfigureAwait(false);
    }

    [Fact]
    public async Task DisposeAsync_NullBody_NoError()
    {
        var exchange = new Exchange(new Message());

        var act = async () => await exchange.DisposeAsync().ConfigureAwait(false);

        await act.Should().NotThrowAsync().ConfigureAwait(false);
    }

    [Fact]
    public async Task DisposeAsync_OutBodyStream_BothDisposed()
    {
        var inStream = new MemoryStream(new byte[] { 1, 2, 3 });
        var outStream = new MemoryStream(new byte[] { 4, 5, 6 });
        var exchange = new Exchange(new Message(inStream))
        {
            Out = new Message(outStream)
        };

        await exchange.DisposeAsync().ConfigureAwait(false);

        inStream.CanRead.Should().BeFalse();
        outStream.CanRead.Should().BeFalse();
    }

    [Fact]
    public async Task DisposeAsync_CustomDisposableBody_CallsDispose()
    {
        var disposable = new TrackingDisposable();
        var exchange = new Exchange(new Message(disposable));

        await exchange.DisposeAsync().ConfigureAwait(false);

        disposable.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task DisposeAsync_CustomAsyncDisposableBody_CallsDisposeAsync()
    {
        var disposable = new TrackingAsyncDisposable();
        var exchange = new Exchange(new Message(disposable));

        await exchange.DisposeAsync().ConfigureAwait(false);

        disposable.Disposed.Should().BeTrue();
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class TrackingAsyncDisposable : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
