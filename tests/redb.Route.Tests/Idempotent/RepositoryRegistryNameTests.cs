using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Idempotent;

/// <summary>
/// Idempotent and claim-check repositories are found as every other registry reference is: by the bare name, checked
/// for type (Camel's lookupByNameAndType). A bean declared in markup under <c>dedup</c> is the repository
/// <c>repository="#dedup"</c> names; no <c>idempotent:</c> or <c>claimcheck:</c> key prefix exists.
/// </summary>
public class RepositoryRegistryNameTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    // ── Idempotent ────────────────────────────────────────────────────────

    [Fact]
    public void A_repository_registered_under_its_bare_name_is_found()
    {
        var repo = new InMemoryIdempotentRepository();
        _context.AddToRegistry("dedup", repo);

        _context.GetIdempotentRepositoryProvider().Get("dedup").Should().BeSameAs(repo);
        _context.GetIdempotentRepositoryProvider().Get("#dedup").Should().BeSameAs(repo, "#name is the registry key as is");
    }

    [Fact]
    public void AddIdempotentRepository_registers_under_the_bare_name()
    {
        var repo = new InMemoryIdempotentRepository();
        _context.AddIdempotentRepository("orders", repo);

        _context.GetFromRegistry<IIdempotentRepository>("orders").Should().BeSameAs(repo);
    }

    [Fact]
    public void Nothing_under_the_name_is_named_as_such_with_the_ways_to_register()
    {
        var act = () => _context.GetIdempotentRepositoryProvider().Get("missing");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Nothing is registered under 'missing'*AddIdempotentRepository*<bean*")
            .Which.Message.Should().NotContain("idempotent:");
    }

    [Fact]
    public void Another_type_under_the_name_is_named_by_its_type()
    {
        _context.AddToRegistry("dedup", "not a repository");

        var act = () => _context.GetIdempotentRepositoryProvider().Get("dedup");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'dedup'*System.String*not an IIdempotentRepository*");
    }

    [Fact]
    public void TryGet_is_false_for_nothing_and_loud_for_another_type()
    {
        _context.GetIdempotentRepositoryProvider().TryGet("missing", out _).Should().BeFalse();

        _context.AddToRegistry("dedup", 42);
        var act = () => _context.GetIdempotentRepositoryProvider().TryGet("dedup", out _);
        act.Should().Throw<InvalidOperationException>().WithMessage("*System.Int32*");
    }

    // ── Claim check ───────────────────────────────────────────────────────

    [Fact]
    public void A_claim_check_repository_is_found_by_its_bare_name()
    {
        var repo = new InMemoryClaimCheckRepository();
        _context.AddToRegistry("payloads", repo);

        _context.ResolveClaimCheckRepository("payloads").Should().BeSameAs(repo);
    }

    [Fact]
    public void AddClaimCheckRepository_registers_under_the_bare_name()
    {
        var repo = new InMemoryClaimCheckRepository();
        _context.AddClaimCheckRepository("payloads", repo);

        _context.GetFromRegistry<IClaimCheckRepository>("payloads").Should().BeSameAs(repo);
    }

    [Fact]
    public void Another_type_under_a_claim_check_name_is_named_by_its_type()
    {
        _context.AddToRegistry("payloads", new InMemoryIdempotentRepository());

        var act = () => _context.ResolveClaimCheckRepository("payloads");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'payloads'*InMemoryIdempotentRepository*not an IClaimCheckRepository*");
    }

    [Fact]
    public void The_default_claim_check_repository_is_a_context_service_not_a_registry_key()
    {
        var repo = new InMemoryClaimCheckRepository();
        _context.SetDefaultClaimCheckRepository(repo);

        _context.ResolveClaimCheckRepository().Should().BeSameAs(repo);
        _context.GetService<IClaimCheckRepository>().Should().BeSameAs(repo);
        _context.GetFromRegistry<object>("claimcheck:__default").Should().BeNull();
    }

    [Fact]
    public void Without_a_default_every_step_shares_one_in_memory_repository()
    {
        var first = _context.ResolveClaimCheckRepository();
        var second = _context.ResolveClaimCheckRepository();

        first.Should().BeOfType<InMemoryClaimCheckRepository>().And.BeSameAs(second);
        _context.GetFromRegistry<object>("claimcheck:__default").Should().BeNull();
    }
}
