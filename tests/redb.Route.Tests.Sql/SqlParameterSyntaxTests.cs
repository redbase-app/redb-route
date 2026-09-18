using System.Data.Common;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Sql;
using redb.Route.Tests.Sql.Batch.Fakes;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql;

/// <summary>
/// Parameters are written as in Apache Camel, <c>:#name</c>, in every statement the connector sends — the single statement,
/// a batch, the poll query, a function call — and <c>placeholderStyle</c> decides what the provider receives: <c>@name</c>
/// (the default), <c>:name</c> (Oracle) or <c>?</c> (ODBC), with one parameter per occurrence where the style is positional.
/// </summary>
public sealed class SqlParameterSyntaxTests : IDisposable
{
    private readonly SqliteTestHelper _db = new();

    public SqlParameterSyntaxTests()
    {
        _db.Execute("CREATE TABLE syntax_items (id INTEGER PRIMARY KEY, val TEXT)");
        _db.Execute("INSERT INTO syntax_items (id, val) VALUES (1, 'a'), (2, 'b')");
    }

    public void Dispose() => _db.Dispose();

    // ── Syntax ──────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_ColonHashPlaceholder_BoundFromHeader()
    {
        var (exchange, thrown) = await ProduceAsync("SELECT val FROM syntax_items WHERE id = :#id", new() { ["outputType"] = "Scalar" },
            e => e.In.Headers["id"] = 2);

        thrown.Should().BeNull(Outcome.Describe(thrown));
        exchange.In.Body.Should().Be("b");
    }

    [Fact]
    public async Task Execute_PlaceholderInsideLiteralAndComment_IsNotBound()
    {
        var (exchange, thrown) = await ProduceAsync("SELECT ':#id' || val FROM syntax_items WHERE id = :#id -- :#other",
            new() { ["outputType"] = "Scalar" }, e => e.In.Headers["id"] = 1);

        thrown.Should().BeNull("':#id' is text and '-- :#other' a comment, so neither needs a value: " + Outcome.Describe(thrown));
        exchange.In.Body.Should().Be(":#ida");
    }

    [Theory]
    [InlineData("SELECT :#${header.id}")]
    [InlineData("SELECT val FROM syntax_items WHERE id IN (:#in:ids)")]
    public async Task Execute_UnsupportedCamelForms_AreRefused(string sql)
    {
        var (_, thrown) = await ProduceAsync(sql, new() { ["outputType"] = "Scalar" }, e => e.In.Headers["id"] = 1);

        thrown.Should().BeOfType<InvalidOperationException>(Outcome.Describe(thrown))
            .Which.Message.Should().Contain(":#", "the message names the placeholder form that is not supported");
    }

    [Fact]
    public async Task Poll_QueryPlaceholder_BoundFromParam()
    {
        var (received, thrown) = await PollAsync("SELECT val FROM syntax_items WHERE id > :#since ORDER BY id",
            new() { ["param.since"] = "1" });

        thrown.Should().BeNull(Outcome.Describe(thrown));
        received.Should().Equal("b");
    }

    [Fact]
    public async Task Poll_QueryPlaceholderWithoutParam_IsAnError()
    {
        var (_, thrown) = await PollAsync("SELECT val FROM syntax_items WHERE id > :#since ORDER BY id", new());

        thrown.Should().BeOfType<InvalidOperationException>(Outcome.Describe(thrown))
            .Which.Message.Should().Contain(":#since", "a poll query takes its values from param.* only");
    }

    // ── Placeholder styles ──────────────────────────────────────────

    /// <remarks>
    /// Placeholders are not repeated here: <c>Colon</c> adds a parameter per occurrence for ODP.NET's positional binding, and
    /// Microsoft.Data.Sqlite, which binds <c>:name</c> by name, refuses two parameters with one name (measured 2026-09-15).
    /// The per-occurrence parameters are covered by the plan's tests.
    /// </remarks>
    [Fact]
    public async Task ColonStyle_ProviderReceivesColonNames()
    {
        var (exchange, thrown) = await ProduceAsync("SELECT :#a + :#b", new() { ["outputType"] = "Scalar", ["placeholderStyle"] = "Colon" },
            e =>
            {
                e.In.Headers["a"] = 2;
                e.In.Headers["b"] = 3;
            });

        thrown.Should().BeNull(Outcome.Describe(thrown));
        Convert.ToInt64(exchange.In.Body).Should().Be(5, "SQLite reads :a and :b");
    }

    [Fact]
    public async Task QuestionStyle_BatchSendsPositionalTextAndOneParameterPerOccurrence()
    {
        var texts = new List<string>();
        var values = new List<object?>();
        var connection = new FakeBatchConnection
        {
            SupportsBatch = true,
            OnExecuteBatch = (_, batch) =>
            {
                foreach (var command in batch.BatchCommands)
                {
                    texts.Add(command.CommandText);
                    values.AddRange(command.Parameters.Cast<DbParameter>().Select(p => p.Value));
                }
                return batch.BatchCommands.Count;
            },
        };
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, new FixedConnectionFactory(connection),
            "INSERT INTO t (a, b, c) VALUES (:#b, :#a, :#b)",
            new() { ["outputType"] = "None", ["batchSize"] = "10", ["placeholderStyle"] = "Question" });
        var items = new List<Dictionary<string, object?>> { new() { ["a"] = 1, ["b"] = 2 } };

        var thrown = await Outcome.Of(() => endpoint.CreateProducer().Process(new Exchange(new Message(items)), CancellationToken.None));

        thrown.Should().BeNull(Outcome.Describe(thrown));
        texts.Should().Equal("INSERT INTO t (a, b, c) VALUES (?, ?, ?)");
        values.Should().Equal(new object?[] { 2, 1, 2 }, "positional parameters follow the occurrences, repeats included");
    }

    // ── Contract details ────────────────────────────────────────────

    [Fact]
    public async Task Execute_RepeatedNameInAnotherCase_IsBoundOnce()
    {
        var (exchange, thrown) = await ProduceAsync("SELECT :#Id + :#id", new() { ["outputType"] = "Scalar" },
            e => e.In.Headers["Id"] = 2);

        thrown.Should().BeNull("names are compared ignoring case, so the provider gets one name: " + Outcome.Describe(thrown));
        Convert.ToInt64(exchange.In.Body).Should().Be(4);
    }

    [Fact]
    public async Task Execute_MissingValue_MessageNamesThePlaceholderAsWritten()
    {
        var (_, thrown) = await ProduceAsync("SELECT :#missing", new() { ["outputType"] = "Scalar" }, _ => { });

        thrown.Should().BeOfType<InvalidOperationException>(Outcome.Describe(thrown))
            .Which.Message.Should().StartWith("SQL parameter ':#missing' has no value", "@ is not a placeholder any more");
    }

    [Fact]
    public async Task Poll_QueryPlaceholderWithExpressionParam_IsAnError()
    {
        var (_, thrown) = await PollAsync("SELECT val FROM syntax_items WHERE id > :#since ORDER BY id",
            new() { ["param.since"] = "${header.x}" });

        thrown.Should().BeOfType<InvalidOperationException>("a poll query has no exchange to evaluate an expression in, " +
            "and the template text must not be bound as the value: " + Outcome.Describe(thrown))
            .Which.Message.Should().Contain("since").And.Contain("${");
    }

    [Fact]
    public async Task Poll_DynamicQuery_IsAnError()
    {
        var (_, thrown) = await PollAsync("SELECT val FROM syntax_items ORDER BY id", new() { ["query"] = "${header.q}" });

        thrown.Should().BeOfType<InvalidOperationException>("a poll has no exchange to resolve query=${...} against, " +
            "and must not fall back to the URI path silently: " + Outcome.Describe(thrown))
            .Which.Message.Should().Contain("query");
    }

    // ── Backslash escapes (MySQL, MariaDB) ──────────────────────────

    [Fact]
    public async Task BackslashEscapes_QuoteEscapedWithBackslash_PlaceholderAfterTheLiteralIsBound()
    {
        var texts = await BatchTextsAsync(@"INSERT INTO t (val, id) VALUES ('it\'s :#no', :#id)", new() { ["backslashEscapes"] = "true" });

        texts.Should().ContainSingle().Which.Should().Be(@"INSERT INTO t (val, id) VALUES ('it\'s :#no', @id)",
            "with backslashEscapes=true \\' stays inside the literal, as MySQL reads it");
    }

    [Fact]
    public async Task BackslashEscapes_Default_BackslashIsAnOrdinaryCharacter()
    {
        var texts = await BatchTextsAsync(@"INSERT INTO t (val, id) VALUES ('C:\', :#id)", new());

        texts.Should().ContainSingle().Which.Should().Be(@"INSERT INTO t (val, id) VALUES ('C:\', @id)",
            "standard SQL (PostgreSQL, SQL Server, Oracle, SQLite) ends 'C:\\' at its second quote");
    }

    [Fact]
    public async Task FunctionCall_HonoursPlaceholderStyle()
    {
        var connection = new FakeBatchConnection();
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, new FixedConnectionFactory(connection), "fn", new()
        {
            ["mode"] = "Procedure",
            ["asFunction"] = "true",
            ["procedureParams"] = "IN:x:Int32",
            ["placeholderStyle"] = "Colon",
        });
        var exchange = new Exchange(new Message());
        exchange.In.Headers["x"] = 3;

        var thrown = await Outcome.Of(() => endpoint.CreateProducer().Process(exchange, CancellationToken.None));

        thrown.Should().BeNull(Outcome.Describe(thrown));
        connection.ExecutedCommandTexts.Should().Equal("SELECT fn(:x)");
    }

    // ── Helpers ─────────────────────────────────────────────────────

    /// <summary>Runs a one-item batch of <paramref name="sql"/> on a fake connection; returns the command texts it received.</summary>
    private static async Task<List<string>> BatchTextsAsync(string sql, Dictionary<string, string> extraParameters)
    {
        var texts = new List<string>();
        var connection = new FakeBatchConnection
        {
            SupportsBatch = true,
            OnExecuteBatch = (_, batch) =>
            {
                texts.AddRange(batch.BatchCommands.Select(c => c.CommandText));
                return batch.BatchCommands.Count;
            },
        };
        var parameters = new Dictionary<string, string>(extraParameters) { ["outputType"] = "None", ["batchSize"] = "10" };
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, new FixedConnectionFactory(connection), sql, parameters);
        var items = new List<Dictionary<string, object?>> { new() { ["id"] = 7 } };

        var thrown = await Outcome.Of(() => endpoint.CreateProducer().Process(new Exchange(new Message(items)), CancellationToken.None));

        thrown.Should().BeNull(Outcome.Describe(thrown));
        return texts;
    }

    private async Task<(Exchange Exchange, Exception? Thrown)> ProduceAsync(
        string sql, Dictionary<string, string> parameters, Action<Exchange> prepare)
    {
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, _db.CreateFactory(), sql, parameters);
        var exchange = new Exchange(new Message());
        prepare(exchange);
        var thrown = await Outcome.Of(() => endpoint.CreateProducer().Process(exchange, CancellationToken.None));
        return (exchange, thrown);
    }

    private async Task<(List<object?> Received, Exception? Thrown)> PollAsync(string sql, Dictionary<string, string> extra)
    {
        await using var context = new RouteContext();
        var parameters = new Dictionary<string, string> { ["mode"] = "Poll", ["repeatCount"] = "1" };
        foreach (var (key, value) in extra)
            parameters[key] = value;
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, _db.CreateFactory(), sql, parameters);
        var received = new List<object?>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                received.Add(((Dictionary<string, object?>)ci.Arg<IExchange>().In.Body!)["val"]);
                return Task.CompletedTask;
            });

        var thrown = await Outcome.Of(() => ((SqlConsumer)endpoint.CreateConsumer(processor)).Poll(CancellationToken.None));
        return (received, thrown);
    }
}
