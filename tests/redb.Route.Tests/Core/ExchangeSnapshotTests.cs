using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// Tests for the checkpoint-snapshot primitive <see cref="IExchange.Snapshot"/> /
/// <see cref="IMessage.Snapshot"/>: unlike <see cref="IExchange.Clone"/> the body is deep-copied,
/// so a snapshot is frozen against later in-place mutation of the payload.
/// </summary>
public class ExchangeSnapshotTests
{
    // A mutable reference body that opts into deep copy via ICloneable.
    private sealed class Cart : ICloneable
    {
        public List<string> Items { get; init; } = [];
        public object Clone() => new Cart { Items = [.. Items] };
    }

    [Fact]
    public void Snapshot_FreezesMutableBody_CloneDoesNot()
    {
        var cart = new Cart { Items = { "a" } };
        var ex = new Exchange(new Message(cart));

        var snap = ex.Snapshot();
        var clone = ex.Clone();

        // mutate the live body AFTER capturing
        cart.Items.Add("b");

        ((Cart)snap.In.Body!).Items.Should().ContainSingle().And.Contain("a");   // snapshot frozen
        ((Cart)clone.In.Body!).Items.Should().HaveCount(2);                       // clone shares reference
    }

    [Fact]
    public void Snapshot_CopiesByteArray()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var ex = new Exchange(new Message(bytes));

        var snap = ex.Snapshot();
        bytes[0] = 99;

        ((byte[])snap.In.Body!).Should().Equal(1, 2, 3);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData(42)]
    [InlineData(true)]
    public void Snapshot_SharesImmutableBodies_AsIs(object body)
    {
        var ex = new Exchange(new Message(body));
        var snap = ex.Snapshot();
        snap.In.Body.Should().Be(body);
    }

    [Fact]
    public void Snapshot_ThrowsForNonSnapshotableBody()
    {
        // plain POCO, not ICloneable, not immutable
        var ex = new Exchange(new Message(new { Name = "x" }));
        var act = () => ex.Snapshot();
        act.Should().Throw<NotSupportedException>().WithMessage("*cannot deep-copy*");
    }

    [Fact]
    public void Snapshot_DeepCopiesOut_AndPreservesMetadata()
    {
        var ex = new Exchange(new Message("in-body"))
        {
            Pattern = ExchangePattern.InOut,
            RouteId = "route-x",
            Out = new Message(new byte[] { 7, 8 })
        };
        ex.Properties["k"] = "v";

        var snap = ex.Snapshot();

        snap.Pattern.Should().Be(ExchangePattern.InOut);
        snap.RouteId.Should().Be("route-x");
        snap.ExchangeId.Should().Be(ex.ExchangeId);         // snapshot keeps identity, like Clone
        snap.Properties["k"].Should().Be("v");
        ((byte[])snap.Out!.Body!).Should().Equal(7, 8);
    }

    [Fact]
    public void Snapshot_HeaderValuesShared_LikeClone()
    {
        var msg = new Message("body");
        msg.Headers["h"] = "hv";
        var ex = new Exchange(msg);

        var snap = ex.Snapshot();

        snap.In.Headers["h"].Should().Be("hv");
        snap.In.Headers.Should().NotBeSameAs(ex.In.Headers);   // new container
    }

    [Fact]
    public void Snapshot_NullBody_Ok()
    {
        var ex = new Exchange(new Message((object?)null));
        var snap = ex.Snapshot();
        snap.In.Body.Should().BeNull();
    }
}
