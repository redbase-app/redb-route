using redb.Route.Sql;

namespace redb.Route.Tests.Sql;

public class SqlNamedQueryRegistryTests
{
    // ── Register + Resolve ──────────────────────────────────────────

    [Fact]
    public void Register_And_Resolve()
    {
        var registry = new SqlNamedQueryRegistry();
        registry.Register("findOrders", "SELECT * FROM orders WHERE status = 'new'");

        var sql = registry.Resolve("findOrders");

        sql.Should().Be("SELECT * FROM orders WHERE status = 'new'");
    }

    [Fact]
    public void Resolve_CaseInsensitive()
    {
        var registry = new SqlNamedQueryRegistry();
        registry.Register("FindOrders", "SELECT 1");

        registry.Resolve("findorders").Should().Be("SELECT 1");
        registry.Resolve("FINDORDERS").Should().Be("SELECT 1");
    }

    [Fact]
    public void Resolve_NotFound_Throws()
    {
        var registry = new SqlNamedQueryRegistry();

        var act = () => registry.Resolve("nonexistent");

        act.Should().Throw<KeyNotFoundException>().WithMessage("*nonexistent*");
    }

    [Fact]
    public void Register_Duplicate_Throws()
    {
        var registry = new SqlNamedQueryRegistry();
        registry.Register("q1", "SELECT 1");

        var act = () => registry.Register("q1", "SELECT 2");

        act.Should().Throw<ArgumentException>().WithMessage("*q1*already*");
    }

    [Fact]
    public void Register_DuplicateCaseInsensitive_Throws()
    {
        var registry = new SqlNamedQueryRegistry();
        registry.Register("MyQuery", "SELECT 1");

        var act = () => registry.Register("myquery", "SELECT 2");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Register_NullName_Throws()
    {
        var registry = new SqlNamedQueryRegistry();

        var act = () => registry.Register(null!, "SELECT 1");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Register_EmptyName_Throws()
    {
        var registry = new SqlNamedQueryRegistry();

        var act = () => registry.Register("", "SELECT 1");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Register_NullSql_Throws()
    {
        var registry = new SqlNamedQueryRegistry();

        var act = () => registry.Register("q", null!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Register_EmptySql_Throws()
    {
        var registry = new SqlNamedQueryRegistry();

        var act = () => registry.Register("q", "");

        act.Should().Throw<ArgumentException>();
    }

    // ── TryResolve ──────────────────────────────────────────────────

    [Fact]
    public void TryResolve_Found_ReturnsTrue()
    {
        var registry = new SqlNamedQueryRegistry();
        registry.Register("q1", "SELECT 1");

        var found = registry.TryResolve("q1", out var sql);

        found.Should().BeTrue();
        sql.Should().Be("SELECT 1");
    }

    [Fact]
    public void TryResolve_NotFound_ReturnsFalse()
    {
        var registry = new SqlNamedQueryRegistry();

        var found = registry.TryResolve("nonexistent", out var sql);

        found.Should().BeFalse();
        sql.Should().BeNull();
    }

    // ── GetAll ──────────────────────────────────────────────────────

    [Fact]
    public void GetAll_Empty()
    {
        var registry = new SqlNamedQueryRegistry();

        registry.GetAll().Should().BeEmpty();
    }

    [Fact]
    public void GetAll_ReturnsAllRegistered()
    {
        var registry = new SqlNamedQueryRegistry();
        registry.Register("q1", "SELECT 1");
        registry.Register("q2", "SELECT 2");

        var all = registry.GetAll();

        all.Should().HaveCount(2);
        all.Should().ContainKey("q1");
        all.Should().ContainKey("q2");
    }

    // ── ResolveRef ──────────────────────────────────────────────────

    [Fact]
    public void ResolveRef_WithPrefix_ResolvesQuery()
    {
        var registry = new SqlNamedQueryRegistry();
        registry.Register("findOrders", "SELECT * FROM orders");

        var result = registry.ResolveRef("ref:findOrders");

        result.Should().Be("SELECT * FROM orders");
    }

    [Fact]
    public void ResolveRef_WithoutPrefix_ReturnsSameSql()
    {
        var registry = new SqlNamedQueryRegistry();

        var result = registry.ResolveRef("SELECT 1");

        result.Should().Be("SELECT 1");
    }

    [Fact]
    public void ResolveRef_CaseInsensitive_Prefix()
    {
        var registry = new SqlNamedQueryRegistry();
        registry.Register("q1", "SELECT 1");

        var result = registry.ResolveRef("REF:q1");

        result.Should().Be("SELECT 1");
    }

    [Fact]
    public void ResolveRef_UnknownName_Throws()
    {
        var registry = new SqlNamedQueryRegistry();

        var act = () => registry.ResolveRef("ref:unknown");

        act.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void RefPrefix_IsRefColon()
    {
        SqlNamedQueryRegistry.RefPrefix.Should().Be("ref:");
    }

    // ── Thread safety ───────────────────────────────────────────────

    [Fact]
    public void ConcurrentRegisterAndResolve_NoErrors()
    {
        var registry = new SqlNamedQueryRegistry();

        // Pre-register
        for (var i = 0; i < 100; i++)
            registry.Register($"q{i}", $"SELECT {i}");

        // Concurrent resolve
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        Parallel.For(0, 100, i =>
        {
            try
            {
                var sql = registry.Resolve($"q{i}");
                sql.Should().Be($"SELECT {i}");
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        });

        errors.Should().BeEmpty();
    }
}
