using redb.Route.Firebase;

namespace redb.Route.Tests.Firebase;

/// <summary>
/// Ф11 Г6 (решение владельца по синтаксису Where): строковый литерал — в одинарных кавычках
/// (<c>status=='007'</c> строго строка), <c>array-contains</c> распознаётся только как токен
/// с пробелами, поле с подстрокой "-contains" в имени больше не рвётся.
/// </summary>
public sealed class FirestoreQueryHelperTests
{
    [Fact]
    public void ParseCondition_QuotedValue_StaysString()
    {
        var (field, op, value) = FirestoreQueryHelper.ParseCondition("status=='007'");

        field.Should().Be("status");
        op.Should().Be("==");
        FirestoreQueryHelper.ParseValue(value).Should().Be("007",
            "литерал в кавычках — строго строка, без коэрции в int");
    }

    [Fact]
    public void ParseValue_Unquoted_KeepsTypeInference()
    {
        FirestoreQueryHelper.ParseValue("007").Should().Be(7);
        FirestoreQueryHelper.ParseValue("true").Should().Be(true);
        FirestoreQueryHelper.ParseValue("3.5").Should().Be(3.5);
        FirestoreQueryHelper.ParseValue("plain").Should().Be("plain");
    }

    [Fact]
    public void ParseCondition_FieldContainingArrayContains_NotTornApart()
    {
        var (field, op, value) = FirestoreQueryHelper.ParseCondition("my-array-contains-x==5");

        field.Should().Be("my-array-contains-x",
            "подстрока 'array-contains' в имени поля не имеет права рвать условие");
        op.Should().Be("==");
        value.Should().Be("5");
    }

    [Fact]
    public void ParseCondition_ArrayContains_SpacedToken()
    {
        var (field, op, value) = FirestoreQueryHelper.ParseCondition("tags array-contains 'admin'");

        field.Should().Be("tags");
        op.Should().Be("array-contains");
        FirestoreQueryHelper.ParseValue(value).Should().Be("admin");
    }

    [Theory]
    [InlineData("age>=18", "age", ">=", "18")]
    [InlineData("age<=18", "age", "<=", "18")]
    [InlineData("age!=18", "age", "!=", "18")]
    [InlineData("age>18", "age", ">", "18")]
    [InlineData("age<18", "age", "<", "18")]
    public void ParseCondition_ComparisonOperators_Unchanged(string condition, string field, string op, string value)
    {
        var parsed = FirestoreQueryHelper.ParseCondition(condition);
        parsed.Should().Be((field, op, value));
    }
}
