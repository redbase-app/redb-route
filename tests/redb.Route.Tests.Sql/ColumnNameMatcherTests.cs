using System.ComponentModel.DataAnnotations.Schema;
using redb.Route.Sql.Mapping;

namespace redb.Route.Tests.Sql;

/// <summary>
/// The name rule shared by row mapping and batch item binding, pinned as a grid: the rule moved out of
/// <see cref="PocoRowMapper{T}"/> unchanged.
/// </summary>
public sealed class ColumnNameMatcherTests
{
    [Theory]
    [InlineData("UserId", "UserId")]
    [InlineData("userid", "UserId")]
    [InlineData("USERID", "UserId")]
    [InlineData("user_id", "UserId")]
    [InlineData("full_name", "Name")]
    [InlineData("FULL_NAME", "Name")]
    [InlineData("name", "Name")]
    [InlineData("email", "Email")]
    [InlineData("email_raw", "email_raw")]
    [InlineData("missing", null)]
    [InlineData("_", null)]
    public void ColumnNameMatcher_SameRuleAsRowMapping(string name, string? expected)
    {
        var found = ColumnNameMatcher.Find(typeof(Target).GetProperties(), name);

        (found?.Name).Should().Be(expected);
    }

    private sealed class Target
    {
        public int UserId { get; set; }

        [Column("full_name")]
        public string? Name { get; set; }

        public string? Email { get; set; }

        // ReSharper disable once InconsistentNaming — a property named like a column
        public string? email_raw { get; set; }
    }
}
