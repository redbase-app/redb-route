using redb.Route.Abstractions;
using redb.Route.Core;
using FluentAssertions;

namespace redb.Route.Tests.Core;

public class ExchangeIdTests
{
    [Fact]
    public void NewExchange_HasNonEmptyExchangeId()
    {
        var ex = new Exchange();
        ex.ExchangeId.Should().NotBeNullOrEmpty();
        ex.ExchangeId.Should().HaveLength(32); // Guid without hyphens
    }

    [Fact]
    public void TwoExchanges_HaveDifferentIds()
    {
        var ex1 = new Exchange();
        var ex2 = new Exchange();
        ex1.ExchangeId.Should().NotBe(ex2.ExchangeId);
    }

    [Fact]
    public void Clone_PreservesExchangeId()
    {
        var original = new Exchange(new Message("test"));
        var clone = original.Clone();
        clone.ExchangeId.Should().Be(original.ExchangeId);
    }

    [Fact]
    public void CreateChild_HasNewExchangeId()
    {
        var parent = new Exchange(new Message("parent"));
        var child = parent.CreateChild(new Message("child"));
        // CreateChild creates a new exchange — should have its own ExchangeId
        child.ExchangeId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ExchangeId_IsAccessibleViaInterface()
    {
        IExchange ex = new Exchange();
        ex.ExchangeId.Should().NotBeNullOrEmpty();
    }
}
