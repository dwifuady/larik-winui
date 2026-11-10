using Larik.Data.MongoDB;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using Xunit;

namespace Larik.Data.Tests;

/// <summary>
/// Golden tests for the SQL-on-MongoDB translator. They pin the exact pipeline a T-SQL statement
/// compiles to, so a refactor that silently changes a query shape shows up as a diff.
/// </summary>
public sealed class TSqlMongoCompilerTests
{
    private static BsonDocument Doc(string json) => BsonDocument.Parse(json);

    private static string Canon(BsonDocument doc) => doc.ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.CanonicalExtendedJson });

    private static void AssertSelect(string sql, string[] expectedStages, string[]? expectedColumns = null)
    {
        var command = TSqlMongoCompiler.Parse(sql);
        var select = Assert.IsType<MSelect>(command);

        Assert.Equal(expectedStages.Length, select.Pipeline.Count);
        for (var i = 0; i < expectedStages.Length; i++)
        {
            Assert.Equal(Canon(Doc(expectedStages[i])), Canon(select.Pipeline[i]));
        }

        if (expectedColumns is null)
        {
            Assert.Null(select.Columns);
        }
        else
        {
            Assert.Equal(expectedColumns, select.Columns);
        }
    }

    // ------------------------------------------------------------------ SELECT

    [Fact]
    public void Select_star_returns_the_whole_document_and_no_projection()
    {
        var command = TSqlMongoCompiler.Parse("SELECT * FROM [users]");
        var select = Assert.IsType<MSelect>(command);

        Assert.Equal("users", select.Collection);
        Assert.Empty(select.Pipeline);
        Assert.Null(select.Columns);
    }

    [Fact]
    public void Select_projects_the_named_column()
    {
        AssertSelect(
            "SELECT [name] FROM [users]",
            ["{ \"$project\": { \"_id\": 0, \"name\": \"$name\" } }"],
            ["name"]);
    }

    [Fact]
    public void Where_limit_then_project()
    {
        AssertSelect(
            "SELECT [name] FROM [users] WHERE [status] = 'active' LIMIT 5",
            [
                "{ \"$match\": { \"status\": \"active\" } }",
                "{ \"$limit\": 5 }",
                "{ \"$project\": { \"_id\": 0, \"name\": \"$name\" } }",
            ],
            ["name"]);
    }

    [Fact]
    public void Comparison_operators_become_query_operators()
    {
        AssertSelect(
            "SELECT [id] FROM [users] WHERE [age] >= 18 AND [age] < 65",
            [
                "{ \"$match\": { \"$and\": [ { \"age\": { \"$gte\": 18 } }, { \"age\": { \"$lt\": 65 } } ] } }",
                "{ \"$project\": { \"_id\": 0, \"id\": \"$id\" } }",
            ],
            ["id"]);
    }

    [Fact]
    public void Null_and_in_map_to_query_terms()
    {
        AssertSelect(
            "SELECT [id] FROM [users] WHERE [email] IS NULL AND [role] IN ('admin', 'ops')",
            [
                "{ \"$match\": { \"$and\": [ { \"email\": null }, { \"role\": { \"$in\": [ \"admin\", \"ops\" ] } } ] } }",
                "{ \"$project\": { \"_id\": 0, \"id\": \"$id\" } }",
            ],
            ["id"]);
    }

    [Fact]
    public void Top_sets_a_limit()
    {
        AssertSelect(
            "SELECT TOP 10 [name] FROM [people]",
            [
                "{ \"$limit\": 10 }",
                "{ \"$project\": { \"_id\": 0, \"name\": \"$name\" } }",
            ],
            ["name"]);
    }

    [Fact]
    public void Group_by_with_aggregate_and_sort()
    {
        AssertSelect(
            "SELECT [city], COUNT(*) AS [n] FROM [users] GROUP BY [city] ORDER BY [city]",
            [
                "{ \"$group\": { \"_id\": \"$city\", \"count\": { \"$sum\": 1 } } }",
                "{ \"$sort\": { \"_id\": 1 } }",
                "{ \"$project\": { \"_id\": 0, \"city\": \"$_id\", \"n\": \"$count\" } }",
            ],
            ["city", "n"]);
    }

    [Fact]
    public void Whole_collection_aggregate()
    {
        AssertSelect(
            "SELECT COUNT(*) AS [total] FROM [users]",
            [
                "{ \"$group\": { \"_id\": null, \"count\": { \"$sum\": 1 } } }",
                "{ \"$project\": { \"_id\": 0, \"total\": \"$count\" } }",
            ],
            ["total"]);
    }

    [Fact]
    public void Left_join_becomes_lookup_and_unwind()
    {
        AssertSelect(
            "SELECT * FROM [orders] o LEFT JOIN [customers] c ON o.customer_id = c.id",
            [
                "{ \"$lookup\": { \"from\": \"customers\", \"localField\": \"customer_id\", \"foreignField\": \"id\", \"as\": \"c\" } }",
                "{ \"$unwind\": { \"path\": \"$c\", \"preserveNullAndEmptyArrays\": true } }",
            ]);
    }

    [Fact]
    public void Inner_join_does_not_preserve_null_arrays()
    {
        var select = Assert.IsType<MSelect>(TSqlMongoCompiler.Parse(
            "SELECT * FROM [orders] o JOIN [customers] c ON o.customer_id = c.id"));

        var unwind = select.Pipeline[1];
        Assert.False(unwind["$unwind"]["preserveNullAndEmptyArrays"].AsBoolean);
    }

    // ------------------------------------------------------------------ writes

    [Fact]
    public void Insert_maps_named_columns_to_fields()
    {
        var command = TSqlMongoCompiler.Parse("INSERT INTO [users] ([name], [age]) VALUES ('alice', 30)");
        var insert = Assert.IsType<MInsert>(command);

        Assert.Equal("users", insert.Collection);
        var doc = Assert.Single(insert.Documents);
        Assert.Equal("alice", doc["name"].AsString);
        Assert.Equal(30, doc["age"].AsInt32);
    }

    [Fact]
    public void Update_sets_fields_and_filters()
    {
        var command = TSqlMongoCompiler.Parse("UPDATE [orders] SET [status] = 'paid' WHERE [id] = 1");
        var update = Assert.IsType<MUpdate>(command);

        Assert.Equal(Canon(Doc("{ \"id\": 1 }")), Canon(update.Filter));
        Assert.Equal(Canon(Doc("{ \"status\": \"paid\" }")), Canon(update.Update));
    }

    [Fact]
    public void Delete_filters_by_where()
    {
        var command = TSqlMongoCompiler.Parse("DELETE FROM [orders] WHERE [id] = 1");
        var delete = Assert.IsType<MDelete>(command);

        Assert.Equal("orders", delete.Collection);
        Assert.Equal(Canon(Doc("{ \"id\": 1 }")), Canon(delete.Filter));
    }

    [Fact]
    public void Use_switches_the_database()
    {
        var command = TSqlMongoCompiler.Parse("USE [reports]");
        Assert.Equal("reports", Assert.IsType<MUse>(command).Database);
    }

    // ------------------------------------------------------------------ unsupported

    [Theory]
    [InlineData("SELECT 1 UNION SELECT 2")]
    [InlineData("SELECT * FROM (SELECT 1) AS x")]
    [InlineData("SELECT * FROM a a INNER JOIN b b ON a.id = b.id AND a.x = b.x")]
    [InlineData("SELECT name FROM users ORDER BY 1")]
    public void Unsupported_constructs_are_rejected(string sql)
    {
        Assert.Throws<TranslationException>(() => TSqlMongoCompiler.Parse(sql));
    }

    [Fact]
    public void Arithmetic_filter_falls_back_to_expr_and_supports_logical_operators()
    {
        AssertSelect(
            "SELECT [id] FROM [t] WHERE [a] + 1 > [b] OR [c] = 1",
            [
                "{ \"$match\": { \"$expr\": { \"$or\": [ { \"$gt\": [ { \"$add\": [ \"$a\", 1 ] }, \"$b\" ] }, { \"$eq\": [ \"$c\", 1 ] } ] } } }",
                "{ \"$project\": { \"_id\": 0, \"id\": \"$id\" } }",
            ],
            ["id"]);
    }

    [Fact]
    public void Round_uses_the_array_form()
    {
        AssertSelect(
            "SELECT ROUND([price], 2) AS [p] FROM [items]",
            ["{ \"$project\": { \"_id\": 0, \"p\": { \"$round\": [ \"$price\", 2 ] } } }"],
            ["p"]);
    }

    [Fact]
    public void Uuid_literal_becomes_a_standard_uuid_binary()
    {
        var guid = Guid.Parse("f903436c-37b7-4b41-87fd-791fa6739cd6");
        var select = Assert.IsType<MSelect>(TSqlMongoCompiler.Parse(
            "SELECT * FROM [journeys] AS [j] WHERE j._id = UUID('f903436c-37b7-4b41-87fd-791fa6739cd6')"));

        var value = select.Pipeline[0]["$match"].AsBsonDocument["_id"];
        Assert.Equal(BsonType.Binary, value.BsonType);
        Assert.Equal(BsonBinarySubType.UuidStandard, value.AsBsonBinaryData.SubType);
        Assert.Equal(guid, new Guid(value.AsBsonBinaryData.Bytes.AsSpan(), bigEndian: true));
    }

    [Fact]
    public void A_sampled_uuid_field_coerces_a_string_literal()
    {
        var guid = Guid.Parse("f903436c-37b7-4b41-87fd-791fa6739cd6");
        var types = new Dictionary<string, string> { ["_id"] = "uuid" };

        var select = Assert.IsType<MSelect>(TSqlMongoCompiler.Parse(
            "SELECT * FROM [journeys] AS [j] WHERE j._id = 'f903436c-37b7-4b41-87fd-791fa6739cd6'",
            types));

        var value = select.Pipeline[0]["$match"].AsBsonDocument["_id"];
        Assert.Equal(BsonType.Binary, value.BsonType);
        Assert.Equal(guid, new Guid(value.AsBsonBinaryData.Bytes.AsSpan(), bigEndian: true));
    }
}