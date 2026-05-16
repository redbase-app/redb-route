using System.Data;
using redb.Route.Sql;
using redb.Route.Expressions;
using redb.Route.Expressions;
using SqlDsl = redb.Route.Sql.Sql;

namespace redb.Route.Tests.Sql;

public class SqlBuilderTests
{
    private static ConstantExpression C(string s) => new(s);

    // ── Factory methods ─────────────────────────────────────────────

    [Fact]
    public void Poll_SetsMode()
    {
        var uri = SqlDsl.Poll("SELECT * FROM t").DataSource(C("main")).Build();
        uri.Should().Contain("mode=Poll");
    }

    [Fact]
    public void Execute_SetsMode()
    {
        var uri = SqlDsl.Execute("INSERT INTO t(x) VALUES(1)").DataSource(C("main")).Build();
        uri.Should().Contain("mode=Execute");
    }

    [Fact]
    public void Procedure_SetsMode()
    {
        var uri = SqlDsl.Procedure("sp_Calc").DataSource(C("main")).Build();
        uri.Should().Contain("mode=Procedure");
    }

    [Fact]
    public void NullQuery_Throws()
    {
        var act = () => SqlDsl.Poll(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EmptyQuery_Throws()
    {
        var act = () => SqlDsl.Execute("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WhitespaceQuery_Throws()
    {
        var act = () => SqlDsl.Execute("   ");
        act.Should().Throw<ArgumentException>();
    }

    // ── URI structure ───────────────────────────────────────────────

    [Fact]
    public void Build_StartsWithSqlScheme()
    {
        var uri = SqlDsl.Execute("SELECT 1").DataSource(C("main")).Build();
        uri.Should().StartWith("sql:");
    }

    [Fact]
    public void Build_ContainsSqlInPath()
    {
        var uri = SqlDsl.Execute("SELECT * FROM orders").DataSource(C("main")).Build();
        uri.Should().StartWith("sql:SELECT * FROM orders");
    }

    [Fact]
    public void Build_FirstParamAfterQuestionMark()
    {
        var uri = SqlDsl.Execute("SELECT 1").DataSource(C("main")).Build();
        uri.Should().Contain("?mode=");
    }

    // ── Connection ──────────────────────────────────────────────────

    [Fact]
    public void DataSource_SetsParam()
    {
        var uri = SqlDsl.Execute("SELECT 1").DataSource(C("main")).Build();
        uri.Should().Contain("dataSource=main");
    }

    [Fact]
    public void ConnectionString_SetsParam()
    {
        var uri = SqlDsl.Execute("SELECT 1").ConnectionString(C("Server=localhost")).Build();
        uri.Should().Contain("connectionString=");
    }

    [Fact]
    public void Provider_SetsParam()
    {
        var uri = SqlDsl.Execute("SELECT 1").DataSource(C("main")).Provider("Npgsql").Build();
        uri.Should().Contain("provider=Npgsql");
    }

    [Fact]
    public void CommandTimeout_SetsParam()
    {
        var uri = SqlDsl.Execute("SELECT 1").DataSource(C("main")).CommandTimeout(120).Build();
        uri.Should().Contain("commandTimeout=120");
    }

    // ── Transaction ─────────────────────────────────────────────────

    [Fact]
    public void Transacted_SetsParam()
    {
        var uri = SqlDsl.Execute("INSERT INTO t(x) VALUES(1)").DataSource(C("main")).Transacted().Build();
        uri.Should().Contain("transacted=true");
    }

    [Fact]
    public void WithIsolationLevel_SetsParam()
    {
        var uri = SqlDsl.Execute("SELECT 1").DataSource(C("main"))
            .WithIsolationLevel(IsolationLevel.Serializable).Build();
        uri.Should().Contain("isolationLevel=Serializable");
    }

    // ── Output ──────────────────────────────────────────────────────

    [Fact]
    public void OutputType_SetsParam()
    {
        var uri = SqlDsl.Execute("SELECT 1").DataSource(C("main")).OutputType(SqlOutputType.Scalar).Build();
        uri.Should().Contain("outputType=Scalar");
    }

    [Fact]
    public void OutputClass_SetsParam()
    {
        var uri = SqlDsl.Execute("SELECT * FROM t").DataSource(C("main")).OutputClass("MyPoco").Build();
        uri.Should().Contain("outputClass=MyPoco");
    }

    [Fact]
    public void OutputHeader_SetsParam()
    {
        var uri = SqlDsl.Execute("SELECT 1").DataSource(C("main")).OutputHeader(C("myHeader")).Build();
        uri.Should().Contain("outputHeader=myHeader");
    }

    [Fact]
    public void Noop_SetsParam()
    {
        var uri = SqlDsl.Execute("SELECT 1").DataSource(C("main")).Noop().Build();
        uri.Should().Contain("noop=true");
    }

    // ── Polling ─────────────────────────────────────────────────────

    [Fact]
    public void Delay_SetsParam()
    {
        var uri = SqlDsl.Poll("SELECT 1").DataSource(C("main")).Delay(5000).Build();
        uri.Should().Contain("delay=5000");
    }

    [Fact]
    public void InitialDelay_SetsParam()
    {
        var uri = SqlDsl.Poll("SELECT 1").DataSource(C("main")).InitialDelay(3000).Build();
        uri.Should().Contain("initialDelay=3000");
    }

    [Fact]
    public void FixedRate_SetsParam()
    {
        var uri = SqlDsl.Poll("SELECT 1").DataSource(C("main")).FixedRate().Build();
        uri.Should().Contain("fixedRate=true");
    }

    [Fact]
    public void RepeatCount_SetsParam()
    {
        var uri = SqlDsl.Poll("SELECT 1").DataSource(C("main")).RepeatCount(100).Build();
        uri.Should().Contain("repeatCount=100");
    }

    [Fact]
    public void MaxMessagesPerPoll_SetsParam()
    {
        var uri = SqlDsl.Poll("SELECT 1").DataSource(C("main")).MaxMessagesPerPoll(50).Build();
        uri.Should().Contain("maxMessagesPerPoll=50");
    }

    [Fact]
    public void RouteEmptyResultSet_SetsParam()
    {
        var uri = SqlDsl.Poll("SELECT 1").DataSource(C("main")).RouteEmptyResultSet().Build();
        uri.Should().Contain("routeEmptyResultSet=true");
    }

    [Fact]
    public void SendEmptyMessageWhenIdle_SetsParam()
    {
        var uri = SqlDsl.Poll("SELECT 1").DataSource(C("main")).SendEmptyMessageWhenIdle().Build();
        uri.Should().Contain("sendEmptyMessageWhenIdle=true");
    }

    // ── Lifecycle SQL ───────────────────────────────────────────────

    [Fact]
    public void OnSuccess_SetsParam()
    {
        var uri = SqlDsl.Poll("SELECT * FROM outbox")
            .DataSource(C("main"))
            .OnSuccess("UPDATE outbox SET done=1 WHERE id=@id")
            .Build();

        uri.Should().Contain("onSuccess=");
    }

    [Fact]
    public void OnFailure_SetsParam()
    {
        var uri = SqlDsl.Poll("SELECT 1")
            .DataSource(C("main"))
            .OnFailure("INSERT INTO err(msg) VALUES(@redbError)")
            .Build();

        uri.Should().Contain("onFailure=");
    }

    [Fact]
    public void OnBatchComplete_SetsParam()
    {
        var uri = SqlDsl.Poll("SELECT 1")
            .DataSource(C("main"))
            .OnBatchComplete("EXEC sp_Notify")
            .Build();

        uri.Should().Contain("onBatchComplete=");
    }

    // ── Batch ───────────────────────────────────────────────────────

    [Fact]
    public void Batch_SetsParam()
    {
        var uri = SqlDsl.Execute("INSERT INTO t(x) VALUES(@x)").DataSource(C("main")).Batch(100).Build();
        uri.Should().Contain("batchSize=100");
    }

    [Fact]
    public void BreakBatchOnError_SetsParam()
    {
        var uri = SqlDsl.Execute("INSERT INTO t(x) VALUES(@x)")
            .DataSource(C("main")).BreakBatchOnError().Build();
        uri.Should().Contain("breakBatchOnError=true");
    }

    // ── Procedure ───────────────────────────────────────────────────

    [Fact]
    public void AsFunction_SetsParam()
    {
        var uri = SqlDsl.Procedure("fn_Calc").DataSource(C("main")).AsFunction().Build();
        uri.Should().Contain("asFunction=true");
    }

    [Fact]
    public void In_AddsParamDef()
    {
        var uri = SqlDsl.Procedure("sp_Test")
            .DataSource(C("main"))
            .In("userId", DbType.Int32)
            .Build();

        uri.Should().Contain("procedureParams=");
        uri.Should().Contain("IN");
        uri.Should().Contain("userId");
    }

    [Fact]
    public void In_WithExpression_AddsParamDef()
    {
        var uri = SqlDsl.Procedure("sp_Test")
            .DataSource(C("main"))
            .In("userId", DbType.Int32, "${header.userId}")
            .Build();

        uri.Should().Contain("procedureParams=");
    }

    [Fact]
    public void Out_AddsParamDef()
    {
        var uri = SqlDsl.Procedure("sp_Test")
            .DataSource(C("main"))
            .Out("result", DbType.String)
            .Build();

        uri.Should().Contain("OUT");
        uri.Should().Contain("result");
    }

    [Fact]
    public void InOut_AddsParamDef()
    {
        var uri = SqlDsl.Procedure("sp_Test")
            .DataSource(C("main"))
            .InOut("counter", DbType.Int32)
            .Build();

        uri.Should().Contain("INOUT");
        uri.Should().Contain("counter");
    }

    [Fact]
    public void MultipleParams_SerializeCorrectly()
    {
        var uri = SqlDsl.Procedure("sp_Test")
            .DataSource(C("main"))
            .In("id", DbType.Int32)
            .Out("name", DbType.String)
            .InOut("count", DbType.Int64)
            .Build();

        // Should contain comma-separated param defs
        uri.Should().Contain("procedureParams=");
    }

    // ── Implicit conversion ─────────────────────────────────────────

    [Fact]
    public void ImplicitStringConversion_Works()
    {
        string uri = SqlDsl.Execute("SELECT 1").DataSource(C("main"));
        uri.Should().StartWith("sql:");
    }

    [Fact]
    public void ToString_SameasBuild()
    {
        var builder = SqlDsl.Execute("SELECT 1").DataSource(C("main"));
        builder.ToString().Should().Be(builder.Build());
    }

    // ── Full fluent chain ───────────────────────────────────────────

    [Fact]
    public void FullPollChain_GeneratesValidUri()
    {
        var uri = SqlDsl.Poll("SELECT * FROM outbox WHERE processed = 0")
            .DataSource(C("main"))
            .Delay(5000)
            .InitialDelay(1000)
            .Transacted()
            .MaxMessagesPerPoll(100)
            .OnSuccess("UPDATE outbox SET processed = 1 WHERE id = @id")
            .OnFailure("INSERT INTO errors(msg) VALUES(@redbError)")
            .Build();

        uri.Should().StartWith("sql:SELECT * FROM outbox");
        uri.Should().Contain("mode=Poll");
        uri.Should().Contain("dataSource=main");
        uri.Should().Contain("delay=5000");
        uri.Should().Contain("initialDelay=1000");
        uri.Should().Contain("transacted=true");
        uri.Should().Contain("maxMessagesPerPoll=100");
        uri.Should().Contain("onSuccess=");
        uri.Should().Contain("onFailure=");
    }

    [Fact]
    public void FullExecuteChain_GeneratesValidUri()
    {
        var uri = SqlDsl.Execute("INSERT INTO audit(event) VALUES(@event)")
            .DataSource(C("main"))
            .CommandTimeout(120)
            .Transacted()
            .WithIsolationLevel(IsolationLevel.Serializable)
            .OutputType(SqlOutputType.None)
            .Build();

        uri.Should().StartWith("sql:INSERT INTO audit");
        uri.Should().Contain("mode=Execute");
        uri.Should().Contain("commandTimeout=120");
        uri.Should().Contain("transacted=true");
        uri.Should().Contain("isolationLevel=Serializable");
        uri.Should().Contain("outputType=None");
    }

    [Fact]
    public void FullProcedureChain_GeneratesValidUri()
    {
        var uri = SqlDsl.Procedure("sp_ProcessOrder")
            .DataSource(C("main"))
            .In("orderId", DbType.Int32)
            .Out("total", DbType.Decimal)
            .Transacted()
            .Build();

        uri.Should().StartWith("sql:sp_ProcessOrder");
        uri.Should().Contain("mode=Procedure");
        uri.Should().Contain("dataSource=main");
        uri.Should().Contain("transacted=true");
        uri.Should().Contain("procedureParams=");
    }

    // ── URL encoding ────────────────────────────────────────────────

    [Fact]
    public void SpecialCharsInSql_PreservedInPath()
    {
        var sql = "SELECT * FROM t WHERE x > 5 AND y < 10";
        var uri = SqlDsl.Execute(sql).DataSource(C("main")).Build();
        uri.Should().StartWith($"sql:{sql}");
    }

    [Fact]
    public void OnSuccess_WithSpecialChars_UrlEncoded()
    {
        var onSuccess = "UPDATE t SET done=1 WHERE id=@id AND status='OK'";
        var uri = SqlDsl.Poll("SELECT 1").DataSource(C("main")).OnSuccess(onSuccess).Build();
        // The value should be URL-encoded
        uri.Should().Contain("onSuccess=");
        uri.Should().NotContain("onSuccess=" + onSuccess); // should be encoded
    }

    // ── Explicit Parameters (.Param) ────────────────────────────────

    [Fact]
    public void Param_WithAtPrefix_SetsParamInUri()
    {
        var uri = SqlDsl.Execute("INSERT INTO t(x) VALUES(@x)")
            .DataSource(C("main"))
            .Param("@x", 42)
            .Build();
        uri.Should().Contain("param.x=42");
    }

    [Fact]
    public void Param_WithoutAtPrefix_SetsParamInUri()
    {
        var uri = SqlDsl.Execute("INSERT INTO t(x) VALUES(@x)")
            .DataSource(C("main"))
            .Param("x", "hello")
            .Build();
        uri.Should().Contain("param.x=hello");
    }

    [Fact]
    public void Param_MultipleParams_AllPresent()
    {
        var uri = SqlDsl.Execute("INSERT INTO t(a,b,c) VALUES(@a,@b,@c)")
            .DataSource(C("main"))
            .Param("@a", 1)
            .Param("@b", "text")
            .Param("@c", true)
            .Build();
        uri.Should().Contain("param.a=1");
        uri.Should().Contain("param.b=text");
        uri.Should().Contain("param.c=True");
    }

    [Fact]
    public void Param_NullValue_SetsEmptyString()
    {
        var uri = SqlDsl.Execute("INSERT INTO t(x) VALUES(@x)")
            .DataSource(C("main"))
            .Param("@x", (object?)null)
            .Build();
        uri.Should().Contain("param.x=");
    }

    [Fact]
    public void Param_Chainable()
    {
        var builder = SqlDsl.Execute("SELECT 1").DataSource(C("main"));
        var result = builder.Param("@x", 1);
        result.Should().BeSameAs(builder);
    }

    // ── Param with IExpression ─────────────────────────────────────────

    [Fact]
    public void Param_WithExpression_SetsTemplateString()
    {
        var uri = SqlDsl.Execute("UPDATE t SET x=@x WHERE id=@id")
            .DataSource(C("main"))
            .Param("@x", new HeaderExpression("myValue"))
            .Build();
        uri.Should().Contain("param.x=%24%7bheader.myValue%7d");
    }

    [Fact]
    public void Param_WithConstantExpression_SetsValue()
    {
        var uri = SqlDsl.Execute("UPDATE t SET x=@x WHERE id=@id")
            .DataSource(C("main"))
            .Param("@x", C("42"))
            .Build();
        uri.Should().Contain("param.x=42");
    }

    [Fact]
    public void Param_Expression_Chainable()
    {
        var builder = SqlDsl.Execute("SELECT 1").DataSource(C("main"));
        var result = builder.Param("@x", new HeaderExpression("val"));
        result.Should().BeSameAs(builder);
    }
}
