using System.Data;
using redb.Route.Core;
using redb.Route.Sql;

namespace redb.Route.Tests.Sql;

public class SqlEndpointOptionsTests
{
    // ── Defaults ────────────────────────────────────────────────────

    [Fact]
    public void Defaults_AreCorrect()
    {
        var o = new SqlEndpointOptions();

        o.Mode.Should().Be(SqlMode.Execute);
        o.DataSource.Should().BeNull();
        o.ConnectionString.Should().BeNull();
        o.Provider.Should().BeNull();
        o.CommandTimeout.Should().Be(30);
        o.Transacted.Should().BeFalse();
        o.IsolationLevel.Should().BeNull();
        o.ReadOnly.Should().BeFalse("the primary database unless the endpoint declares readOnly=true");
        o.PlaceholderStyle.Should().Be(SqlPlaceholderStyle.At, ":#name is sent as @name unless the provider needs another style");
        o.BackslashEscapes.Should().BeFalse("standard SQL literals unless the endpoint declares backslashEscapes=true");
        o.Query.Should().BeNull();
        o.OutputType.Should().Be(SqlOutputType.Auto);
        o.OutputClass.Should().BeNull();
        o.OutputHeader.Should().BeNull();
        o.Noop.Should().BeFalse();
        o.Delay.Should().Be(500);
        o.InitialDelay.Should().Be(1000);
        o.FixedRate.Should().BeFalse();
        o.RepeatCount.Should().Be(0);
        o.MaxMessagesPerPoll.Should().Be(-1);
        o.RouteEmptyResultSet.Should().BeFalse();
        o.SendEmptyMessageWhenIdle.Should().BeFalse();
        o.OnSuccess.Should().BeNull();
        o.OnFailure.Should().BeNull();
        o.OnBatchComplete.Should().BeNull();
        o.PollDelivery.Should().Be(SqlPollDelivery.PerRow, "an exchange per row unless a list is asked for, as Camel's useIterator=true");
        o.BatchSize.Should().Be(0);
        o.BreakBatchOnError.Should().BeTrue("a failing batch item rolls the whole batch back unless told otherwise, as in Camel");
        o.ProcedureName.Should().BeNull();
        o.AsFunction.Should().BeFalse();
        o.ProcedureParams.Should().BeNull();
    }

    // ── Validate ────────────────────────────────────────────────────

    [Fact]
    public void Validate_NoDataSourceOrConnectionString_Throws()
    {
        var o = new SqlEndpointOptions();

        var act = () => o.Validate();

        act.Should().Throw<ArgumentException>().WithMessage("*DataSource*ConnectionString*");
    }

    [Fact]
    public void Validate_WithDataSource_Valid()
    {
        var o = new SqlEndpointOptions { DataSource = "main" };
        o.Validate(); // no throw
    }

    [Fact]
    public void Validate_WithConnectionString_Valid()
    {
        var o = new SqlEndpointOptions { ConnectionString = "Server=localhost" };
        o.Validate(); // no throw
    }

    [Fact]
    public void Validate_PollWithoutQuery_Valid()
    {
        // Query is optional in options — it can come from URI path
        var o = new SqlEndpointOptions { Mode = SqlMode.Poll, DataSource = "main" };
        o.Validate(); // no throw
    }

    [Fact]
    public void Validate_PollWithQuery_Valid()
    {
        var o = new SqlEndpointOptions
        {
            Mode = SqlMode.Poll,
            DataSource = "main",
            Query = DynamicValue<string>.FromStatic("SELECT 1")
        };
        o.Validate(); // no throw
    }

    [Fact]
    public void Validate_ProcedureWithoutName_Throws()
    {
        var o = new SqlEndpointOptions { Mode = SqlMode.Procedure, DataSource = "main" };

        var act = () => o.Validate();

        act.Should().Throw<ArgumentException>().WithMessage("*ProcedureName*Procedure*");
    }

    [Fact]
    public void Validate_ProcedureWithName_Valid()
    {
        var o = new SqlEndpointOptions
        {
            Mode = SqlMode.Procedure,
            DataSource = "main",
            ProcedureName = "sp_Calculate"
        };
        o.Validate(); // no throw
    }

    [Fact]
    public void Validate_NegativeCommandTimeout_Throws()
    {
        var o = new SqlEndpointOptions { DataSource = "main", CommandTimeout = -1 };

        var act = () => o.Validate();

        act.Should().Throw<ArgumentException>().WithMessage("*CommandTimeout*");
    }

    [Fact]
    public void Validate_NegativeDelay_Throws()
    {
        var o = new SqlEndpointOptions { DataSource = "main", Delay = -1 };

        var act = () => o.Validate();

        act.Should().Throw<ArgumentException>().WithMessage("*Delay*");
    }

    [Fact]
    public void Validate_ReadOnlyWithBatchSize_Throws()
    {
        var o = new SqlEndpointOptions();
        o.BindFromUri(new Dictionary<string, string> { ["dataSource"] = "main", ["readOnly"] = "true", ["batchSize"] = "10" });

        var act = () => o.Validate();

        act.Should().Throw<ArgumentException>("a batch writes, and readOnly sends it to the replica").WithMessage("*readOnly*batchSize*");
    }

    [Theory]
    [InlineData("onSuccess", "UPDATE t SET done = 1")]
    [InlineData("onFailure", "UPDATE t SET failed = 1")]
    [InlineData("onBatchComplete", "DELETE FROM t WHERE done = 1")]
    [InlineData("transacted", "true")]
    public void Validate_ReadOnlyPollThatMarksRows_Throws(string option, string value)
    {
        var o = new SqlEndpointOptions();
        o.BindFromUri(new Dictionary<string, string>
        {
            ["mode"] = "Poll", ["dataSource"] = "main", ["readOnly"] = "true", [option] = value,
        });

        var act = () => o.Validate();

        act.Should().Throw<ArgumentException>("rows read on a lagging replica and marked on the primary come back")
            .WithMessage("*readOnly*");
    }

    [Fact]
    public void Validate_FunctionWithOutParameters_Throws()
    {
        var o = new SqlEndpointOptions
        {
            DataSource = "main",
            Mode = SqlMode.Procedure,
            ProcedureName = "fn",
            AsFunction = true,
            ProcedureParams = "IN:x:Int32,OUT:r:Int32",
        };

        var act = () => o.Validate();

        act.Should().Throw<ArgumentException>("SELECT fn(...) returns its result as the scalar; an OUT parameter has no place in " +
            "the text and shifts the positions of the others under a positional placeholder style")
            .WithMessage("*OUT*");
    }

    [Theory]
    [InlineData(SqlOutputType.Scalar)]
    [InlineData(SqlOutputType.SelectOne)]
    public void Validate_ListDeliveryWithSingleValueOutput_Throws(SqlOutputType outputType)
    {
        var o = new SqlEndpointOptions
        {
            DataSource = "main",
            Mode = SqlMode.Poll,
            PollDelivery = SqlPollDelivery.List,
            OutputType = outputType,
        };

        var act = () => o.Validate();

        act.Should().Throw<ArgumentException>("a single value is not a list; the option would be ignored silently")
            .WithMessage("*pollDelivery*");
    }

    [Fact]
    public void Validate_ReadOnlyPollWithoutLifecycleSql_Valid()
    {
        var o = new SqlEndpointOptions();
        o.BindFromUri(new Dictionary<string, string> { ["mode"] = "Poll", ["dataSource"] = "main", ["readOnly"] = "true" });

        o.Validate(); // no throw
    }

    // ── BindFromUri ─────────────────────────────────────────────────

    [Fact]
    public void BindFromUri_Mode_ParsesCorrectly()
    {
        var o = new SqlEndpointOptions();
        o.BindFromUri(new Dictionary<string, string> { ["mode"] = "Poll", ["dataSource"] = "main" });

        o.Mode.Should().Be(SqlMode.Poll);
    }

    [Fact]
    public void BindFromUri_OutputType_ParsesCorrectly()
    {
        var o = new SqlEndpointOptions();
        o.BindFromUri(new Dictionary<string, string>
        {
            ["outputType"] = "Scalar",
            ["dataSource"] = "main"
        });

        o.OutputType.Should().Be(SqlOutputType.Scalar);
    }

    [Fact]
    public void BindFromUri_IntProperties()
    {
        var o = new SqlEndpointOptions();
        o.BindFromUri(new Dictionary<string, string>
        {
            ["delay"] = "2000",
            ["initialDelay"] = "500",
            ["commandTimeout"] = "60",
            ["maxMessagesPerPoll"] = "10",
            ["batchSize"] = "50",
            ["dataSource"] = "main"
        });

        o.Delay.Should().Be(2000);
        o.InitialDelay.Should().Be(500);
        o.CommandTimeout.Should().Be(60);
        o.MaxMessagesPerPoll.Should().Be(10);
        o.BatchSize.Should().Be(50);
    }

    [Fact]
    public void BindFromUri_BoolProperties()
    {
        var o = new SqlEndpointOptions();
        o.BindFromUri(new Dictionary<string, string>
        {
            ["transacted"] = "true",
            ["fixedRate"] = "true",
            ["noop"] = "true",
            ["routeEmptyResultSet"] = "true",
            ["sendEmptyMessageWhenIdle"] = "true",
            ["breakBatchOnError"] = "true",
            ["asFunction"] = "true",
            ["dataSource"] = "main"
        });

        o.Transacted.Should().BeTrue();
        o.FixedRate.Should().BeTrue();
        o.Noop.Should().BeTrue();
        o.RouteEmptyResultSet.Should().BeTrue();
        o.SendEmptyMessageWhenIdle.Should().BeTrue();
        o.BreakBatchOnError.Should().BeTrue();
        o.AsFunction.Should().BeTrue();
    }

    [Fact]
    public void BindFromUri_StringProperties()
    {
        var o = new SqlEndpointOptions();
        o.BindFromUri(new Dictionary<string, string>
        {
            ["dataSource"] = "main",
            ["connectionString"] = "Server=localhost",
            ["provider"] = "Microsoft.Data.SqlClient",
            ["outputClass"] = "MyPoco",
            ["outputHeader"] = "resultHeader",
            ["onSuccess"] = "UPDATE t SET done=1 WHERE id=:#id",
            ["onFailure"] = "INSERT INTO errors(msg) VALUES(:#redbError)",
            ["onBatchComplete"] = "EXEC sp_Notify",
            ["procedureName"] = "sp_Process",
            ["procedureParams"] = "IN:id:Int32,OUT:result:String"
        });

        o.DataSource.Should().Be("main");
        o.ConnectionString.Should().Be("Server=localhost");
        o.Provider.Should().Be("Microsoft.Data.SqlClient");
        o.OutputClass.Should().Be("MyPoco");
        o.OutputHeader.Should().Be("resultHeader");
        o.OnSuccess.Should().Be("UPDATE t SET done=1 WHERE id=:#id");
        o.OnFailure.Should().Be("INSERT INTO errors(msg) VALUES(:#redbError)");
        o.OnBatchComplete.Should().Be("EXEC sp_Notify");
        o.ProcedureName.Should().Be("sp_Process");
        o.ProcedureParams.Should().Be("IN:id:Int32,OUT:result:String");
    }

    [Fact]
    public void BindFromUri_RepeatCount_Long()
    {
        var o = new SqlEndpointOptions();
        o.BindFromUri(new Dictionary<string, string>
        {
            ["repeatCount"] = "1000000",
            ["dataSource"] = "main"
        });

        o.RepeatCount.Should().Be(1_000_000);
    }

    [Fact]
    public void BindFromUri_IsolationLevel_Enum()
    {
        var o = new SqlEndpointOptions();
        o.BindFromUri(new Dictionary<string, string>
        {
            ["isolationLevel"] = "ReadCommitted",
            ["dataSource"] = "main"
        });

        o.IsolationLevel.Should().Be(IsolationLevel.ReadCommitted);
    }

    [Fact]
    public void BindFromUri_CaseInsensitive()
    {
        var o = new SqlEndpointOptions();
        o.BindFromUri(new Dictionary<string, string>
        {
            ["MODE"] = "Procedure",
            ["DATASOURCE"] = "main",
            ["PROCEDURENAME"] = "sp_Test"
        });

        o.Mode.Should().Be(SqlMode.Procedure);
        o.DataSource.Should().Be("main");
        o.ProcedureName.Should().Be("sp_Test");
    }

    // ── Explicit Parameters ─────────────────────────────────────────

    [Fact]
    public void ExplicitParameters_ExtractsFromUnmapped()
    {
        var o = new SqlEndpointOptions();
        o.BindFromUri(new Dictionary<string, string>
        {
            ["mode"] = "Execute",
            ["dataSource"] = "main",
            ["param.id"] = "42",
            ["param.name"] = "Alice"
        });

        o.ExplicitParameters.Should().HaveCount(2);
        o.ExplicitParameters["id"].Should().Be("42");
        o.ExplicitParameters["name"].Should().Be("Alice");
    }

    [Fact]
    public void ExplicitParameters_CaseInsensitiveLookup()
    {
        var o = new SqlEndpointOptions();
        o.BindFromUri(new Dictionary<string, string>
        {
            ["dataSource"] = "main",
            ["param.MyParam"] = "val"
        });

        o.ExplicitParameters.TryGetValue("myparam", out var v).Should().BeTrue();
        v.Should().Be("val");
    }

    [Fact]
    public void ExplicitParameters_EmptyWhenNoParams()
    {
        var o = new SqlEndpointOptions();
        o.BindFromUri(new Dictionary<string, string>
        {
            ["dataSource"] = "main"
        });

        o.ExplicitParameters.Should().BeEmpty();
    }

    [Fact]
    public void ExplicitParameters_ExpressionValuePreserved()
    {
        var o = new SqlEndpointOptions();
        o.BindFromUri(new Dictionary<string, string>
        {
            ["dataSource"] = "main",
            ["param.userId"] = "${header.currentUser}"
        });

        o.ExplicitParameters["userId"].Should().Be("${header.currentUser}");
    }
}
