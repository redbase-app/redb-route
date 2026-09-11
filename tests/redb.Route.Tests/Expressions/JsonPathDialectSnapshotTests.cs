using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// Characterization net for the <c>jpath</c> dialect (V4 phase 10): every form of the corpus is
/// evaluated against a fixed document and described as text; the description is compared with the
/// snapshot below line by line. Any change in the engine or the conversion rules shows up here as a
/// named line, not as a surprise in a route.
/// </summary>
[Collection("ExpressionResolver")]
public class JsonPathDialectSnapshotTests
{
    private const string Store = """
        {
          "store": {
            "book": [
              { "category": "reference", "author": "Nigel Rees", "title": "Sayings of the Century", "price": 8.95, "published": "2026-01-15T10:00:00Z" },
              { "category": "fiction", "author": "Evelyn Waugh", "title": "Sword of Honour", "price": 12.99 },
              { "category": "fiction", "author": "Herman Melville", "title": "Moby Dick", "isbn": "0-553-21311-3", "price": 8.99 },
              { "category": "fiction", "author": "J. R. R. Tolkien", "title": "The Lord of the Rings", "isbn": "0-395-19395-8", "price": 22.99 }
            ],
            "bicycle": { "color": "red", "price": 19.95 }
          },
          "expensive": 10,
          "big": 12345678901,
          "ids": [1, 2, 3],
          "tags": ["a", "b"],
          "empty": [],
          "nothing": null,
          "meta": { "guid": "8b0a5e3e-1d4a-4b6d-9d8e-0f1c2a3b4c5d", "when": "2026-09-01T12:00:00+03:00" }
        }
        """;

    public sealed class Poco { public string Name { get; set; } = "Alice"; public int Age { get; set; } = 30; public string[] Tags { get; set; } = ["x", "y"]; }

    private enum BodyKind { Json, Lenient, SingleQuotes, Poco, JsonNode }

    private sealed record Case(string Id, string Path, Type Target, BodyKind Body = BodyKind.Json);

    private static readonly Case[] Corpus =
    [
        new("authors_all", "$.store.book[*].author", typeof(string)),
        new("authors_arr", "$.store.book[*].author", typeof(string[])),
        new("authors_list", "$.store.book[*].author", typeof(List<string>)),
        new("prices_rec_arr", "$..price", typeof(double[])),
        new("prices_rec_obj", "$..price", typeof(object)),
        new("slice", "$.store.book[0:2].title", typeof(string[])),
        new("neg_slice", "$.store.book[-1:].title", typeof(string[])),
        new("filter_lt", "$.store.book[?(@.price < 10)].title", typeof(string[])),
        new("filter_exists", "$.store.book[?(@.isbn)].title", typeof(string[])),
        new("filter_eq", "$.store.book[?(@.category == 'fiction')].author", typeof(List<string>)),
        new("filter_bool_true", "$.store.book[?(@.price > 20)]", typeof(bool)),
        new("filter_bool_false", "$.store.book[?(@.price > 100)]", typeof(bool)),
        new("filter_str_empty", "$.store.book[?(@.price > 100)].title", typeof(string)),
        new("filter_obj", "$.store.book[?(@.price > 20)].title", typeof(object)),
        new("first_book_obj", "$.store.book[0]", typeof(object)),
        new("first_book_dict", "$.store.book[0]", typeof(Dictionary<string, object>)),
        new("bracket", "$['store']['bicycle']['color']", typeof(string)),
        new("int_obj", "$.expensive", typeof(object)),
        new("big_obj", "$.big", typeof(object)),
        new("int_typed", "$.expensive", typeof(int)),
        new("price_obj", "$.store.bicycle.price", typeof(object)),
        new("price_decimal", "$.store.bicycle.price", typeof(decimal)),
        new("price_str", "$.store.bicycle.price", typeof(string)),
        new("ids_int_arr", "$.ids", typeof(int[])),
        new("ids_obj", "$.ids", typeof(object)),
        new("ids_str", "$.ids", typeof(string)),
        new("tags_obj", "$.tags", typeof(object)),
        new("empty_obj", "$.empty", typeof(object)),
        new("null_obj", "$.nothing", typeof(object)),
        new("null_str", "$.nothing", typeof(string)),
        new("missing_str", "$.missing", typeof(string)),
        new("missing_int", "$.missing", typeof(int)),
        new("missing_nint", "$.missing", typeof(int?)),
        new("missing_obj", "$.missing", typeof(object)),
        new("chain_missing", "$.store.missing.deeper", typeof(string)),
        new("date_obj", "$.store.book[0].published", typeof(object)),
        new("date_typed", "$.store.book[0].published", typeof(DateTime)),
        new("date_str", "$.store.book[0].published", typeof(string)),
        new("offset_obj", "$.meta.when", typeof(object)),
        new("guid_obj", "$.meta.guid", typeof(object)),
        new("root_obj", "$", typeof(object)),
        new("root_str", "$", typeof(string)),
        new("wildcard_obj", "$.store.book[*].price", typeof(object)),
        new("bool_obj", "$.store.book[?(@.price > 20)].price", typeof(object)),
        new("lenient_comments", "$.a", typeof(object), BodyKind.Lenient),
        new("single_quotes", "$.a", typeof(object), BodyKind.SingleQuotes),
        new("poco_prop", "$.Name", typeof(string), BodyKind.Poco),
        new("poco_lower", "$.name", typeof(string), BodyKind.Poco),
        new("poco_arr", "$.Tags", typeof(string[]), BodyKind.Poco),
        new("poco_obj", "$.Age", typeof(object), BodyKind.Poco),
        new("jsonnode_str", "$.store.bicycle.color", typeof(string), BodyKind.JsonNode),
        new("jsonnode_obj", "$.ids", typeof(object), BodyKind.JsonNode),
    ];

    /// <summary>Forms that go through the expression language rather than <c>JsonPathExpression</c> directly.</summary>
    private static readonly (string Id, Func<IExchange, object?> Run)[] LanguageCorpus =
    [
        ("tpl_title", e => ExpressionResolver.ProcessTemplate("${jpath($.store.book[1].title)}", e)),
        ("tpl_missing", e => ExpressionResolver.ProcessTemplate("[${jpath($.missing)}]", e)),
        ("tpl_price", e => ExpressionResolver.ProcessTemplate("${jpath($.store.bicycle.price)}", e)),
        ("val_ids", e => ExpressionResolver.ResolveExpression("jpath('$.ids')", e)),
        ("val_filter_hit", e => ExpressionResolver.ResolveExpression("jpath('$.store.book[?(@.price > 20)]')", e)),
        ("val_filter_miss", e => ExpressionResolver.ResolveExpression("jpath('$.store.book[?(@.price > 100)]')", e)),
        ("val_int", e => ExpressionResolver.ResolveExpression("jpath('$.expensive')", e)),
        ("val_arith", e => ExpressionResolver.ResolveExpression("jpath('$.expensive') * 2", e)),
    ];

    [Fact]
    public void Dialect_MatchesSnapshot()
    {
        var actual = Record();
        var expected = Normalize(Snapshot);
        if (actual == expected) return;

        var a = actual.Split('\n');
        var b = expected.Split('\n');
        var diff = new StringBuilder("jpath dialect drift (expected → actual):\n");
        foreach (var line in a.Where(l => !b.Contains(l))) diff.Append("  + ").Append(line).Append('\n');
        foreach (var line in b.Where(l => !a.Contains(l))) diff.Append("  - ").Append(line).Append('\n');
        diff.Append("\n--- full actual ---\n").Append(actual);
        Assert.Fail(diff.ToString());
    }

    private static string Record()
    {
        var sb = new StringBuilder();
        foreach (var c in Corpus)
            sb.Append(c.Id).Append(" | ").Append(TypeName(c.Target)).Append(" | ").Append(Evaluate(c)).Append('\n');
        var exchange = Exchange.Create(new Message(Store), null);
        foreach (var (id, run) in LanguageCorpus)
            sb.Append(id).Append(" | language | ").Append(Guard(() => run(exchange))).Append('\n');
        return Normalize(sb.ToString());
    }

    private static string Evaluate(Case c)
    {
        var exchange = Exchange.Create(new Message(Body(c.Body)), null);
        var method = typeof(JsonPathExpression).GetMethod(nameof(JsonPathExpression.Evaluate))!.MakeGenericMethod(c.Target);
        return Guard(() =>
        {
            try { return method.Invoke(new JsonPathExpression(c.Path), [exchange]); }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null) { throw ex.InnerException; }
        });
    }

    private static object Body(BodyKind kind) => kind switch
    {
        BodyKind.Json => Store,
        BodyKind.Lenient => "{ // comment\n  \"a\": 1, /* block */ \"b\": [1, 2,], }",
        BodyKind.SingleQuotes => "{'a': 1}",
        BodyKind.Poco => new Poco(),
        BodyKind.JsonNode => JsonNode.Parse(Store)!,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string Guard(Func<object?> run)
    {
        try { return Describe(run()); }
        catch (Exception ex) { return "!" + ex.GetType().Name; }
    }

    private static string Describe(object? value) => value switch
    {
        null => "null",
        string s => "\"" + s + "\"",
        bool b => b ? "true" : "false",
        DateTime d => "DateTime:" + d.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
        DateTimeOffset d => "DateTimeOffset:" + d.ToString("o", CultureInfo.InvariantCulture),
        Guid g => "Guid:" + g,
        JsonNode node => node.GetType().Name + ":" + node.ToJsonString(),
        System.Collections.IDictionary dict => "{" + string.Join(", ", dict.Keys.Cast<object>().Select(k => k.ToString())) + "}:" + TypeName(value.GetType()),
        System.Collections.IEnumerable list => "[" + string.Join(", ", list.Cast<object?>().Select(Describe)) + "]:" + TypeName(value.GetType()),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture) + ":" + value.GetType().Name,
        _ => value.ToString() + ":" + TypeName(value.GetType()),
    };

    private static string TypeName(Type t) => t.IsArray ? TypeName(t.GetElementType()!) + "[]"
        : t.IsGenericType ? t.Name[..t.Name.IndexOf('`')] + "<" + string.Join(",", t.GetGenericArguments().Select(TypeName)) + ">"
        : t.Name;

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Trim();

    // Recorded 2026-09-01 on JsonPath.Net 3.0.2 + System.Text.Json right after the migration. The same corpus was
    // recorded on the Newtonsoft engine first; the lines that changed are listed in docs/V4/10-NEWTONSOFT.md §6
    // (dates stay strings, single-quoted JSON rejected, numbers rendered invariant, negative slices and
    // JsonNode bodies now work, arrays/objects to string give JSON text, empty filter to bool gives false).
    private const string Snapshot = """
        authors_all | String | "Nigel Rees, Evelyn Waugh, Herman Melville, J. R. R. Tolkien"
        authors_arr | String[] | ["Nigel Rees", "Evelyn Waugh", "Herman Melville", "J. R. R. Tolkien"]:String[]
        authors_list | List<String> | ["Nigel Rees", "Evelyn Waugh", "Herman Melville", "J. R. R. Tolkien"]:List<String>
        prices_rec_arr | Double[] | [8.95:Double, 12.99:Double, 8.99:Double, 22.99:Double, 19.95:Double]:Double[]
        prices_rec_obj | Object | [8.95:Double, 12.99:Double, 8.99:Double, 22.99:Double, 19.95:Double]:Double[]
        slice | String[] | ["Sayings of the Century", "Sword of Honour"]:String[]
        neg_slice | String[] | ["The Lord of the Rings"]:String[]
        filter_lt | String[] | ["Sayings of the Century", "Moby Dick"]:String[]
        filter_exists | String[] | ["Moby Dick", "The Lord of the Rings"]:String[]
        filter_eq | List<String> | ["Evelyn Waugh", "Herman Melville", "J. R. R. Tolkien"]:List<String>
        filter_bool_true | Boolean | true
        filter_bool_false | Boolean | false
        filter_str_empty | String | null
        filter_obj | Object | "The Lord of the Rings"
        first_book_obj | Object | JsonObject:{"category":"reference","author":"Nigel Rees","title":"Sayings of the Century","price":8.95,"published":"2026-01-15T10:00:00Z"}
        first_book_dict | Dictionary<String,Object> | {category, author, title, price, published}:Dictionary<String,Object>
        bracket | String | "red"
        int_obj | Object | 10:Int64
        big_obj | Object | 12345678901:Int64
        int_typed | Int32 | 10:Int32
        price_obj | Object | 19.95:Double
        price_decimal | Decimal | 19.95:Decimal
        price_str | String | "19.95"
        ids_int_arr | Int32[] | [1:Int32, 2:Int32, 3:Int32]:Int32[]
        ids_obj | Object | [1:Int32, 2:Int32, 3:Int32]:Int32[]
        ids_str | String | "[1,2,3]"
        tags_obj | Object | ["a", "b"]:String[]
        empty_obj | Object | []:Object[]
        null_obj | Object | null
        null_str | String | null
        missing_str | String | null
        missing_int | Int32 | !InvalidOperationException
        missing_nint | Nullable<Int32> | null
        missing_obj | Object | null
        chain_missing | String | null
        date_obj | Object | "2026-01-15T10:00:00Z"
        date_typed | DateTime | DateTime:2026-01-15T10:00:00.0000000Z
        date_str | String | "2026-01-15T10:00:00Z"
        offset_obj | Object | "2026-09-01T12:00:00+03:00"
        guid_obj | Object | "8b0a5e3e-1d4a-4b6d-9d8e-0f1c2a3b4c5d"
        root_obj | Object | JsonObject:{"store":{"book":[{"category":"reference","author":"Nigel Rees","title":"Sayings of the Century","price":8.95,"published":"2026-01-15T10:00:00Z"},{"category":"fiction","author":"Evelyn Waugh","title":"Sword of Honour","price":12.99},{"category":"fiction","author":"Herman Melville","title":"Moby Dick","isbn":"0-553-21311-3","price":8.99},{"category":"fiction","author":"J. R. R. Tolkien","title":"The Lord of the Rings","isbn":"0-395-19395-8","price":22.99}],"bicycle":{"color":"red","price":19.95}},"expensive":10,"big":12345678901,"ids":[1,2,3],"tags":["a","b"],"empty":[],"nothing":null,"meta":{"guid":"8b0a5e3e-1d4a-4b6d-9d8e-0f1c2a3b4c5d","when":"2026-09-01T12:00:00\u002B03:00"}}
        root_str | String | "{"store":{"book":[{"category":"reference","author":"Nigel Rees","title":"Sayings of the Century","price":8.95,"published":"2026-01-15T10:00:00Z"},{"category":"fiction","author":"Evelyn Waugh","title":"Sword of Honour","price":12.99},{"category":"fiction","author":"Herman Melville","title":"Moby Dick","isbn":"0-553-21311-3","price":8.99},{"category":"fiction","author":"J. R. R. Tolkien","title":"The Lord of the Rings","isbn":"0-395-19395-8","price":22.99}],"bicycle":{"color":"red","price":19.95}},"expensive":10,"big":12345678901,"ids":[1,2,3],"tags":["a","b"],"empty":[],"nothing":null,"meta":{"guid":"8b0a5e3e-1d4a-4b6d-9d8e-0f1c2a3b4c5d","when":"2026-09-01T12:00:00\u002B03:00"}}"
        wildcard_obj | Object | [8.95:Double, 12.99:Double, 8.99:Double, 22.99:Double]:Double[]
        bool_obj | Object | 22.99:Double
        lenient_comments | Object | 1:Int64
        single_quotes | Object | !InvalidOperationException
        poco_prop | String | "Alice"
        poco_lower | String | null
        poco_arr | String[] | ["x", "y"]:String[]
        poco_obj | Object | 30:Int64
        jsonnode_str | String | "red"
        jsonnode_obj | Object | [1:Int32, 2:Int32, 3:Int32]:Int32[]
        tpl_title | language | "Sword of Honour"
        tpl_missing | language | "[]"
        tpl_price | language | "19.95"
        val_ids | language | [1:Int32, 2:Int32, 3:Int32]:Int32[]
        val_filter_hit | language | [JsonObject:{"category":"fiction","author":"J. R. R. Tolkien","title":"The Lord of the Rings","isbn":"0-395-19395-8","price":22.99}]:Object[]
        val_filter_miss | language | null
        val_int | language | 10:Int64
        val_arith | language | 20:Int32
        """;
}
