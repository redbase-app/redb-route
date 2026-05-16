using System.Data.Common;
using Microsoft.Data.Sqlite;
using redb.Route.Sql.Mapping;

namespace redb.Route.Tests.Sql;

public class RowMapperTests : IDisposable
{
    private readonly SqliteTestHelper _db = new();

    public RowMapperTests()
    {
        _db.Execute("""
            CREATE TABLE mapper_test (
                id    INTEGER PRIMARY KEY,
                name  TEXT,
                score REAL,
                data  BLOB,
                empty TEXT
            )
            """);

        _db.Execute("INSERT INTO mapper_test(id, name, score, data, empty) VALUES(1, 'Alice', 95.5, X'CAFE', NULL)");
        _db.Execute("INSERT INTO mapper_test(id, name, score, data, empty) VALUES(2, 'Bob', 85.0, X'BABE', NULL)");
    }

    // ── DictionaryRowMapper ─────────────────────────────────────────

    [Fact]
    public void DictionaryMapper_MapsAllColumns()
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM mapper_test WHERE id = 1";
        using var reader = cmd.ExecuteReader();
        reader.Read().Should().BeTrue();

        var mapper = new DictionaryRowMapper();
        var row = mapper.Map(reader);

        row.Should().ContainKey("id");
        row.Should().ContainKey("name");
        row.Should().ContainKey("score");
        row.Should().ContainKey("data");
        row.Should().ContainKey("empty");
    }

    [Fact]
    public void DictionaryMapper_ValuesAreCorrect()
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, score FROM mapper_test WHERE id = 1";
        using var reader = cmd.ExecuteReader();
        reader.Read();

        var mapper = new DictionaryRowMapper();
        var row = mapper.Map(reader);

        row["id"].Should().Be(1L);
        row["name"].Should().Be("Alice");
        row["score"].Should().Be(95.5);
    }

    [Fact]
    public void DictionaryMapper_DBNull_BecomesNull()
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT empty FROM mapper_test WHERE id = 1";
        using var reader = cmd.ExecuteReader();
        reader.Read();

        var mapper = new DictionaryRowMapper();
        var row = mapper.Map(reader);

        row["empty"].Should().BeNull();
    }

    [Fact]
    public void DictionaryMapper_CaseInsensitiveKeys()
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM mapper_test WHERE id = 1";
        using var reader = cmd.ExecuteReader();
        reader.Read();

        var mapper = new DictionaryRowMapper();
        var row = mapper.Map(reader);

        // Keys are case-insensitive
        row["NAME"].Should().Be("Alice");
        row["Name"].Should().Be("Alice");
    }

    [Fact]
    public void DictionaryMapper_MultipleRows()
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name FROM mapper_test ORDER BY id";
        using var reader = cmd.ExecuteReader();

        var mapper = new DictionaryRowMapper();
        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
            rows.Add(mapper.Map(reader));

        rows.Should().HaveCount(2);
        rows[0]["name"].Should().Be("Alice");
        rows[1]["name"].Should().Be("Bob");
    }

    // ── ScalarMapper ────────────────────────────────────────────────

    [Fact]
    public void ScalarMapper_LongValue()
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM mapper_test";
        using var reader = cmd.ExecuteReader();
        reader.Read();

        var mapper = new ScalarMapper<long>();
        var result = mapper.Map(reader);

        result.Should().Be(2L);
    }

    [Fact]
    public void ScalarMapper_StringValue()
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM mapper_test WHERE id = 1";
        using var reader = cmd.ExecuteReader();
        reader.Read();

        var mapper = new ScalarMapper<string>();
        var result = mapper.Map(reader);

        result.Should().Be("Alice");
    }

    [Fact]
    public void ScalarMapper_DBNull_ReturnsDefault()
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT empty FROM mapper_test WHERE id = 1";
        using var reader = cmd.ExecuteReader();
        reader.Read();

        var mapper = new ScalarMapper<string>();
        var result = mapper.Map(reader);

        result.Should().BeNull();
    }

    [Fact]
    public void ScalarMapper_IntConversion()
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM mapper_test WHERE id = 1";
        using var reader = cmd.ExecuteReader();
        reader.Read();

        var mapper = new ScalarMapper<int>();
        var result = mapper.Map(reader);

        result.Should().Be(1);
    }

    // ── PocoRowMapper ───────────────────────────────────────────────

    [Fact]
    public void PocoMapper_MapsProperties_CaseInsensitive()
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, score FROM mapper_test WHERE id = 1";
        using var reader = cmd.ExecuteReader();
        reader.Read();

        var mapper = new PocoRowMapper<TestPoco>();
        var obj = mapper.Map(reader);

        obj.Id.Should().Be(1);
        obj.Name.Should().Be("Alice");
        obj.Score.Should().Be(95.5);
    }

    [Fact]
    public void PocoMapper_DBNull_SkipsProperty()
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, empty as Description FROM mapper_test WHERE id = 1";
        using var reader = cmd.ExecuteReader();
        reader.Read();

        var mapper = new PocoRowMapper<TestPoco>();
        var obj = mapper.Map(reader);

        obj.Description.Should().BeNull();
    }

    [Fact]
    public void PocoMapper_SnakeCase_ToPascalCase()
    {
        _db.Execute("CREATE TABLE snake_test (user_name TEXT, created_at TEXT)");
        _db.Execute("INSERT INTO snake_test VALUES('Alice', '2024-01-01')");

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT user_name, created_at FROM snake_test";
        using var reader = cmd.ExecuteReader();
        reader.Read();

        var mapper = new PocoRowMapper<SnakePoco>();
        var obj = mapper.Map(reader);

        obj.UserName.Should().Be("Alice");
        obj.CreatedAt.Should().Be("2024-01-01");
    }

    [Fact]
    public void PocoMapper_ColumnAttribute()
    {
        _db.Execute("CREATE TABLE col_attr_test (full_name TEXT)");
        _db.Execute("INSERT INTO col_attr_test VALUES('Bob')");

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT full_name FROM col_attr_test";
        using var reader = cmd.ExecuteReader();
        reader.Read();

        var mapper = new PocoRowMapper<ColumnAttrPoco>();
        var obj = mapper.Map(reader);

        obj.Name.Should().Be("Bob");
    }

    [Fact]
    public void PocoMapper_UnknownColumn_Skipped()
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        // 'data' column has no matching property
        cmd.CommandText = "SELECT id, name, data FROM mapper_test WHERE id = 1";
        using var reader = cmd.ExecuteReader();
        reader.Read();

        var mapper = new PocoRowMapper<TestPoco>();
        var obj = mapper.Map(reader);

        obj.Id.Should().Be(1);
        obj.Name.Should().Be("Alice");
    }

    [Fact]
    public void PocoMapper_PropertyCache_IsCaseInsensitive()
    {
        // Call Map twice with same column names to test cache
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name FROM mapper_test ORDER BY id";
        using var reader = cmd.ExecuteReader();

        var mapper = new PocoRowMapper<TestPoco>();

        reader.Read();
        var obj1 = mapper.Map(reader);
        reader.Read();
        var obj2 = mapper.Map(reader);

        obj1.Name.Should().Be("Alice");
        obj2.Name.Should().Be("Bob");
    }

    public void Dispose() => _db.Dispose();

    // ── Test POCOs ──────────────────────────────────────────────────

    private class TestPoco
    {
        public long Id { get; set; }
        public string? Name { get; set; }
        public double Score { get; set; }
        public string? Description { get; set; }
    }

    private class SnakePoco
    {
        public string? UserName { get; set; }
        public string? CreatedAt { get; set; }
    }

    private class ColumnAttrPoco
    {
        [System.ComponentModel.DataAnnotations.Schema.Column("full_name")]
        public string? Name { get; set; }
    }
}
