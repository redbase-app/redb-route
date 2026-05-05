using redb.Route.Sql;

namespace redb.Route.Tests.Sql;

public class SqlEnumsTests
{
    // ── SqlMode ─────────────────────────────────────────────────────

    [Fact]
    public void SqlMode_HasThreeValues()
    {
        Enum.GetValues<SqlMode>().Should().HaveCount(3);
    }

    [Theory]
    [InlineData(SqlMode.Poll, 0)]
    [InlineData(SqlMode.Execute, 1)]
    [InlineData(SqlMode.Procedure, 2)]
    public void SqlMode_Values_AreCorrect(SqlMode mode, int expected)
    {
        ((int)mode).Should().Be(expected);
    }

    [Theory]
    [InlineData("Poll", SqlMode.Poll)]
    [InlineData("Execute", SqlMode.Execute)]
    [InlineData("Procedure", SqlMode.Procedure)]
    [InlineData("poll", SqlMode.Poll)]
    [InlineData("execute", SqlMode.Execute)]
    [InlineData("procedure", SqlMode.Procedure)]
    public void SqlMode_Parse_CaseInsensitive(string text, SqlMode expected)
    {
        Enum.Parse<SqlMode>(text, ignoreCase: true).Should().Be(expected);
    }

    // ── SqlOutputType ───────────────────────────────────────────────

    [Fact]
    public void SqlOutputType_HasSixValues()
    {
        Enum.GetValues<SqlOutputType>().Should().HaveCount(6);
    }

    [Theory]
    [InlineData(SqlOutputType.Auto, 0)]
    [InlineData(SqlOutputType.SelectList, 1)]
    [InlineData(SqlOutputType.SelectOne, 2)]
    [InlineData(SqlOutputType.StreamList, 3)]
    [InlineData(SqlOutputType.Scalar, 4)]
    [InlineData(SqlOutputType.None, 5)]
    public void SqlOutputType_Values_AreCorrect(SqlOutputType type, int expected)
    {
        ((int)type).Should().Be(expected);
    }

    [Theory]
    [InlineData("SelectList", SqlOutputType.SelectList)]
    [InlineData("selectone", SqlOutputType.SelectOne)]
    [InlineData("StreamList", SqlOutputType.StreamList)]
    [InlineData("Scalar", SqlOutputType.Scalar)]
    [InlineData("NONE", SqlOutputType.None)]
    public void SqlOutputType_Parse_CaseInsensitive(string text, SqlOutputType expected)
    {
        Enum.Parse<SqlOutputType>(text, ignoreCase: true).Should().Be(expected);
    }

    // ── SqlParamDirection ───────────────────────────────────────────

    [Fact]
    public void SqlParamDirection_HasThreeValues()
    {
        Enum.GetValues<SqlParamDirection>().Should().HaveCount(3);
    }

    [Theory]
    [InlineData(SqlParamDirection.In, 0)]
    [InlineData(SqlParamDirection.Out, 1)]
    [InlineData(SqlParamDirection.InOut, 2)]
    public void SqlParamDirection_Values_AreCorrect(SqlParamDirection dir, int expected)
    {
        ((int)dir).Should().Be(expected);
    }
}
