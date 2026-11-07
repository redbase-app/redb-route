using System.Globalization;
using redb.Route.Core;
using redb.Route.Sql;
using redb.Route.Sql.Mapping;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql;

/// <summary>
/// A column reaches a typed property only when the conversion is lossless and unambiguous; anything else fails loudly with
/// the column, the property and both types — never the value — as Apache Camel's <c>BeanPropertyRowMapper</c> throws
/// <c>TypeMismatchException</c>. A NULL goes into a nullable or reference property as null and is an error for a value type.
/// </summary>
public sealed class SqlValueMappingStrictTests : IDisposable
{
    private readonly SqliteTestHelper _db = new();

    public void Dispose() => _db.Dispose();

    public enum Kind { None = 0, First = 1, Second = 2 }

    public sealed class Target
    {
        public int Small { get; set; }
        public int? MaybeSmall { get; set; }
        public string? Text { get; set; } = "initial";
        public decimal Amount { get; set; }
        public bool Flag { get; set; }
        public Kind Kind { get; set; }
        public Guid Key { get; set; }
        public DateOnly Day { get; set; }
        public DateTimeOffset At { get; set; }
        public TimeSpan Span { get; set; }
    }

    // ── Errors instead of silent defaults ───────────────────────────

    [Fact]
    public void Null_IntoValueTypeProperty_Throws() =>
        AssertFails("SELECT NULL AS small", "small", "Small");

    [Fact]
    public void Overflow_IntoInt_Throws_WithoutTheValue()
    {
        var error = AssertFails("SELECT 5000000000 AS small", "small", "Small");
        error.Message.Should().NotContain("5000000000", "column values stay out of messages and logs");
    }

    [Fact]
    public void NonNumericText_IntoInt_Throws() =>
        AssertFails("SELECT 'abc' AS small", "small", "Small");

    [Fact]
    public void FractionalReal_IntoInt_Throws() =>
        AssertFails("SELECT 12.5 AS small", "small", "Small");

    [Fact]
    public void IntegerOtherThanZeroOrOne_IntoBool_Throws() =>
        AssertFails("SELECT 2 AS flag", "flag", "Flag");

    [Fact]
    public void RealWithIntegralValue_IntoBool_Mapped() =>
        Map("SELECT 1.0 AS flag").Flag.Should().BeTrue("a numeric column holding 0 or 1 (Oracle NUMBER(1), PostgreSQL numeric) is a Boolean, as an integer one is");

    [Fact]
    public void RealWithIntegralValue_IntoEnum_Mapped() =>
        Map("SELECT 2.0 AS kind").Kind.Should().Be(Kind.Second, "a numeric column holding a member's number is that member");

    [Fact]
    public void FractionalReal_IntoBool_Throws() =>
        AssertFails("SELECT 0.5 AS flag", "flag", "Flag");

    [Fact]
    public void InvalidIsoText_IntoDateTimeOffset_Throws_WithoutTheValue()
    {
        var error = AssertFails("SELECT '2024-13-45T00:00:00+00:00' AS at", "at", "At");
        error.Message.Should().NotContain("2024-13-45", "column values stay out of messages and logs");
    }

    [Fact]
    public void UndefinedEnumValue_Throws() =>
        AssertFails("SELECT 7 AS kind", "kind", "Kind");

    [Fact]
    public void TextWithoutOffset_IntoDateTimeOffset_Throws() =>
        AssertFails("SELECT '2024-01-02 03:04:05' AS at", "at", "At");

    [Fact]
    public async Task ProducerSelectList_UnconvertibleColumn_FailsTheExchange()
    {
        _db.Execute("CREATE TABLE strict_items (id INTEGER PRIMARY KEY, small INTEGER)");
        _db.Execute("INSERT INTO strict_items (id, small) VALUES (1, 5000000000)");
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, _db.CreateFactory(), "SELECT small FROM strict_items",
            new() { ["outputType"] = "SelectList", ["outputClass"] = typeof(Target).AssemblyQualifiedName! });

        var thrown = await Outcome.Of(() => endpoint.CreateProducer().Process(new Exchange(new Message()), CancellationToken.None));

        thrown.Should().BeAssignableTo<InvalidOperationException>(Outcome.Describe(thrown))
            .Which.Message.Should().Contain("small").And.Contain("Small");
    }

    // ── Lossless conversions ────────────────────────────────────────

    [Fact]
    public void Null_IntoNullableAndReferenceProperties_SetsNull()
    {
        var target = Map("SELECT NULL AS maybe_small, NULL AS text");

        target.MaybeSmall.Should().BeNull();
        target.Text.Should().BeNull("a NULL column sets the property, it does not leave the initializer's value");
    }

    [Fact]
    public void EnumFromIntegerAndFromName_Mapped()
    {
        Map("SELECT 2 AS kind").Kind.Should().Be(Kind.Second);
        Map("SELECT 'first' AS kind").Kind.Should().Be(Kind.First);
    }

    [Fact]
    public void TextColumns_IntoGuidDateOnlyTimeSpan_Mapped()
    {
        var target = Map("SELECT '6f9619ff-8b86-d011-b42d-00c04fc964ff' AS key, '2024-01-02' AS day, '01:02:03' AS span");

        target.Key.Should().Be(Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"));
        target.Day.Should().Be(new DateOnly(2024, 1, 2));
        target.Span.Should().Be(new TimeSpan(1, 2, 3));
    }

    [Fact]
    public void TextWithOffset_IntoDateTimeOffset_Mapped()
    {
        Map("SELECT '2024-01-02T03:04:05+03:00' AS at").At
            .Should().Be(new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.FromHours(3)));
    }

    [Fact]
    public void NumericText_ParsedWithInvariantCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
        try
        {
            Map("SELECT '12.5' AS amount").Amount.Should().Be(12.5m, "the database's text does not depend on the process culture");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void LosslessNumbers_Mapped()
    {
        var target = Map("SELECT 12.0 AS small, 12.5 AS amount, 1 AS flag");

        target.Small.Should().Be(12);
        target.Amount.Should().Be(12.5m);
        target.Flag.Should().BeTrue();
    }

    // ── ScalarMapper follows the same contract ──────────────────────

    [Fact]
    public void Scalar_NullIntoValueType_Throws()
    {
        var act = () => MapScalar(new ScalarMapper<int>(), "SELECT NULL");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Scalar_NullIntoNullable_ReturnsNull() =>
        MapScalar(new ScalarMapper<int?>(), "SELECT NULL").Should().BeNull();

    [Fact]
    public void Scalar_Overflow_ThrowsInvalidOperation()
    {
        var act = () => MapScalar(new ScalarMapper<int>(), "SELECT 5000000000");
        act.Should().Throw<InvalidOperationException>();
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private Target Map(string select)
    {
        using var connection = _db.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = select;
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();
        return new PocoRowMapper<Target>().Map(reader);
    }

    private InvalidOperationException AssertFails(string select, string column, string property)
    {
        var act = () => Map(select);
        var error = act.Should().Throw<InvalidOperationException>($"{select} must not map silently").Which;
        error.Message.Should().Contain(column, "the message names the column").And.Contain(property, "and the property");
        return error;
    }

    private T? MapScalar<T>(ScalarMapper<T> mapper, string select)
    {
        using var connection = _db.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = select;
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();
        return mapper.Map(reader);
    }
}
