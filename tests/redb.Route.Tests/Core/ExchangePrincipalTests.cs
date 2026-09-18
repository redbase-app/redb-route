using System.Security.Claims;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// <see cref="ExchangePrincipal"/> — the caller's identity as an exchange property (Camel
/// <c>Exchange.AUTHENTICATION</c>). What makes it worth a well-known key is inheritance: a sub-route,
/// a split part and an LLM tool call all run on derived exchanges, and each must still know who sent
/// the request that started them.
/// </summary>
public sealed class ExchangePrincipalTests
{
    private static ClaimsPrincipal User(string id) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test"));

    [Fact]
    public void A_hand_made_exchange_carries_no_identity()
    {
        ExchangePrincipal.Get(new Exchange(new Message("m"))).Should().BeNull();
    }

    [Fact]
    public void Set_then_get_returns_the_same_principal_and_null_removes_it()
    {
        var exchange = new Exchange(new Message("m"));
        var user = User("u-1");

        ExchangePrincipal.Set(exchange, user);
        ExchangePrincipal.Get(exchange).Should().BeSameAs(user);

        ExchangePrincipal.Set(exchange, null);
        ExchangePrincipal.Get(exchange).Should().BeNull();
        exchange.Properties.Should().NotContainKey(ExchangePrincipal.PropertyKey);
    }

    [Fact]
    public void A_value_of_another_type_under_the_key_is_not_an_identity()
    {
        var exchange = new Exchange(new Message("m"));
        exchange.Properties[ExchangePrincipal.PropertyKey] = "user-42";

        ExchangePrincipal.Get(exchange).Should().BeNull(
            "a string under the key is not a principal, and building one from it would forge an identity from data");
    }

    public static TheoryData<string> Copies => new() { "CreateChild", "CreateLinkedChild", "Clone", "CloneLinked", "Snapshot" };

    [Theory]
    [MemberData(nameof(Copies))]
    public void Every_exchange_copy_inherits_the_identity(string copy)
    {
        var exchange = new Exchange(new Message("m"));
        var user = User("u-7");
        ExchangePrincipal.Set(exchange, user);

        IExchange derived = copy switch
        {
            "CreateChild" => exchange.CreateChild(new Message("part")),
            "CreateLinkedChild" => exchange.CreateLinkedChild(new Message("tool")),
            "Clone" => exchange.Clone(),
            "CloneLinked" => exchange.CloneLinked(),
            "Snapshot" => exchange.Snapshot(),
            _ => throw new ArgumentOutOfRangeException(nameof(copy)),
        };

        ExchangePrincipal.Get(derived).Should().BeSameAs(user, $"{copy} runs code on behalf of the same caller");
    }
}
