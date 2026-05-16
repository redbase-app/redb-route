using System.Transactions;
using redb.Route.Transactions;

namespace redb.Route.Tests.Transactions;

/// <summary>
/// Tests for <see cref="TransactionPolicy"/>.
/// </summary>
public class TransactionPolicyTests
{
    // ── Defaults ──

    [Fact]
    public void Default_HasRequiredScopeOption()
    {
        var policy = TransactionPolicy.Default;

        policy.ScopeOption.Should().Be(TransactionScopeOption.Required);
    }

    [Fact]
    public void Default_Has30SecondTimeout()
    {
        TransactionPolicy.Default.Timeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Default_HasReadCommittedIsolation()
    {
        TransactionPolicy.Default.IsolationLevel.Should().Be(IsolationLevel.ReadCommitted);
    }

    // ── Static policies ──

    [Fact]
    public void RequiresNew_HasCorrectScopeOption()
    {
        TransactionPolicy.RequiresNew.ScopeOption.Should().Be(TransactionScopeOption.RequiresNew);
    }

    [Fact]
    public void Suppress_HasCorrectScopeOption()
    {
        TransactionPolicy.Suppress.ScopeOption.Should().Be(TransactionScopeOption.Suppress);
    }

    // ── Init syntax ──

    [Fact]
    public void Init_AllowsCustomValues()
    {
        var policy = new TransactionPolicy
        {
            ScopeOption = TransactionScopeOption.RequiresNew,
            Timeout = TimeSpan.FromMinutes(5),
            IsolationLevel = IsolationLevel.Serializable
        };

        policy.ScopeOption.Should().Be(TransactionScopeOption.RequiresNew);
        policy.Timeout.Should().Be(TimeSpan.FromMinutes(5));
        policy.IsolationLevel.Should().Be(IsolationLevel.Serializable);
    }

    // ── FromName ──

    [Theory]
    [InlineData("Required", TransactionScopeOption.Required)]
    [InlineData("REQUIRED", TransactionScopeOption.Required)]
    [InlineData("required", TransactionScopeOption.Required)]
    [InlineData(" Required ", TransactionScopeOption.Required)]
    [InlineData("RequiresNew", TransactionScopeOption.RequiresNew)]
    [InlineData("REQUIRESNEW", TransactionScopeOption.RequiresNew)]
    [InlineData("Suppress", TransactionScopeOption.Suppress)]
    [InlineData("SUPPRESS", TransactionScopeOption.Suppress)]
    public void FromName_ParsesKnownPolicies(string name, TransactionScopeOption expected)
    {
        var policy = TransactionPolicy.FromName(name);

        policy.ScopeOption.Should().Be(expected);
    }

    [Fact]
    public void FromName_ThrowsForUnknownName()
    {
        var act = () => TransactionPolicy.FromName("NotAPolicy");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Unknown transaction policy*NotAPolicy*");
    }

    [Fact]
    public void FromName_ThrowsForNullName()
    {
        var act = () => TransactionPolicy.FromName(null!);

        act.Should().Throw<ArgumentException>();
    }

    // ── Mandatory (R-16) ──

    [Theory]
    [InlineData("Mandatory")]
    [InlineData("MANDATORY")]
    [InlineData(" mandatory ")]
    public void FromName_ParsesMandatory(string name)
    {
        var policy = TransactionPolicy.FromName(name);

        policy.Should().BeSameAs(TransactionPolicy.Mandatory);
    }

    [Fact]
    public void Mandatory_CreateScope_ThrowsWhenNoAmbientTransaction()
    {
        // Sanity guard: ensure no ambient transaction leaked from another test.
        Transaction.Current.Should().BeNull();

        var act = () => TransactionPolicy.Mandatory.CreateScope();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Mandatory*ambient*");
    }

    [Fact]
    public void Mandatory_CreateScope_JoinsExistingAmbientTransaction()
    {
        using var outer = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
        Transaction.Current.Should().NotBeNull();
        var ambientId = Transaction.Current!.TransactionInformation.LocalIdentifier;

        using (var inner = TransactionPolicy.Mandatory.CreateScope())
        {
            // Inside the Mandatory scope we must still see the same ambient transaction.
            Transaction.Current.Should().NotBeNull();
            Transaction.Current!.TransactionInformation.LocalIdentifier.Should().Be(ambientId);
            inner.Complete();
        }

        outer.Complete();
    }
}
