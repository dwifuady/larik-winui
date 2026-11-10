using Larik.Data;
using Larik.Data.MongoDB;
using Xunit;

namespace Larik.Data.Tests;

/// <summary>
/// Editor-language golden tests for the MongoDB provider. The document surface is T-SQL (shared token
/// set and statement splitting), with mongosh statements tagged for passthrough.
/// </summary>
public sealed class MongoDialectTests
{
    private static readonly MongoTSqlDialect Mongo = MongoTSqlDialect.Instance;

    private static string[] Statements(string text) =>
        Mongo.Split(text)
            .Select(span => text.Substring(span.Start, span.Length))
            .ToArray();

    // ------------------------------------------------------------------ splitting

    [Fact]
    public void Splits_sql_on_semicolons_and_keeps_the_terminator()
    {
        var spans = Mongo.Split("SELECT 1; SELECT * FROM [users]");

        Assert.Equal(2, spans.Count);
        Assert.Equal("SELECT 1;", Statements("SELECT 1; SELECT * FROM [users]")[0]);
        Assert.Equal("SELECT * FROM [users]", Statements("SELECT 1; SELECT * FROM [users]")[1]);
    }

    [Fact]
    public void No_go_separates_mongo_statements()
    {
        // GO is a SQL Server client separator; a Mongo document has no such batch boundary.
        Assert.Single(Mongo.Split("SELECT 1 GO SELECT 2"));
    }

    [Fact]
    public void Ignores_semicolons_inside_strings_comments_braces_and_brackets()
    {
        Assert.Single(Mongo.Split("SELECT 'a;b';"));
        Assert.Single(Mongo.Split("-- a;b\nSELECT 1"));
        Assert.Single(Mongo.Split("db.users.find({ name: 'a;b' });"));
        Assert.Single(Mongo.Split("SELECT [a;b] FROM [t]"));
    }

    [Fact]
    public void Blocks_and_line_comments_and_brackets_are_supported()
    {
        Assert.Equal(
            ["/* c; */ SELECT 1"],
            Statements("/* c; */ SELECT 1"));
        Assert.Equal(
            ["db.x.find({ q: '// not a comment' });", "-- line\nSELECT 2"],
            Statements("db.x.find({ q: '// not a comment' });\n-- line\nSELECT 2"));
    }

    // ------------------------------------------------------------------ mixed-script tags

    [Fact]
    public void Sql_statements_are_tagged_sql()
    {
        var spans = Mongo.Split("SELECT 1; UPDATE [t] SET [a] = 1 WHERE [b] = 2");

        Assert.All(spans, span => Assert.Equal("sql", span.Language));
    }

    [Fact]
    public void Db_dot_statements_are_tagged_shell()
    {
        var spans = Mongo.Split("db.users.find({ age: 30 }); SELECT 1");

        Assert.Equal("shell", spans[0].Language);
        Assert.Equal("sql", spans[1].Language);
    }

    [Fact]
    public void Mixed_document_runs_in_document_order_with_correct_spans()
    {
        const string text = "db.orders.findOne();\nSELECT TOP 3 [name] FROM [customers];\ndb.cache.drop();";
        var spans = Mongo.Split(text);

        Assert.Equal(3, spans.Count);
        Assert.Equal("shell", spans[0].Language);
        Assert.Equal("sql", spans[1].Language);
        Assert.Equal("shell", spans[2].Language);
        Assert.Equal("SELECT TOP 3 [name] FROM [customers];", text.Substring(spans[1].Start, spans[1].Length));
    }

    // ------------------------------------------------------------------ tokenizing

    [Fact]
    public void Paints_tsql_keywords_and_functions_from_the_shared_set()
    {
        var tokens = Mongo.Tokenize("SELECT COUNT(*) FROM [users] WHERE [age] >= 18");

        Assert.Contains(tokens, token => token is { Kind: QueryTokenKind.Keyword, Start: 0, Length: 6 });
        Assert.Contains(tokens, token => token is { Kind: QueryTokenKind.Function, Start: 7, Length: 5 });
        Assert.Contains(tokens, token => token is { Kind: QueryTokenKind.Keyword, Start: 16, Length: 4 }); // FROM
    }

    [Fact]
    public void String_tokens_include_unicode_prefix_and_both_quote_kinds()
    {
        static string[] Words(string text) => Mongo.Tokenize(text)
            .Select(token => text.Substring(token.Start, token.Length))
            .ToArray();

        Assert.Equal(["N'x'"], Words("N'x'"));
        Assert.Equal(["'a''b'"], Words("'a''b'"));
        Assert.Equal(["\"dq\""], Words("\"dq\""));
    }

    [Fact]
    public void Bracketed_identifiers_are_left_uncoloured()
    {
        Assert.Empty(Mongo.Tokenize("[Order Details]"));
    }

    // ------------------------------------------------------------------ completion metadata

    [Fact]
    public void Completion_metadata_carries_the_sql_surface_and_mongo_helpers()
    {
        Assert.Contains("SELECT", Mongo.Keywords);
        Assert.Contains("LEFT JOIN", Mongo.Keywords);
        Assert.Contains("COUNT", Mongo.Functions);
        Assert.Contains("ObjectId", Mongo.Functions);
        Assert.Contains("FROM", Mongo.TableContextKeywords);
    }

    [Fact]
    public void Mongo_quotes_identifiers_with_brackets()
    {
        Assert.Equal("[users]", Mongo.QuoteIdentifier("users"));
        Assert.Equal("[a]]b]", Mongo.QuoteIdentifier("a]b"));
    }

    [Fact]
    public void Preview_and_lookup_stay_on_the_translated_surface()
    {
        Assert.Equal("SELECT * FROM [users]", Mongo.PreviewStatement(null, "users", 100));

        Assert.Equal(
            "SELECT * FROM [users] WHERE [id] = '7' LIMIT 25",
            Mongo.RowsStatement(null, "users", [new RowFilter("id", "7", "int")], null, 25));

        Assert.Equal(
            "SELECT * FROM [users] LIMIT 200",
            Mongo.RowsStatement(null, "users", [], orderByColumn: null, 200));
    }

    [Fact]
    public void Format_literal_quotes_everything()
    {
        Assert.Equal("'it''s'", Mongo.FormatLiteral("it's", null));
        Assert.Equal("NULL", Mongo.FormatLiteral(null, "int"));
    }

    // ------------------------------------------------------------------ provider wiring

    [Fact]
    public void Mongo_provider_exposes_the_mongo_dialect()
    {
        Assert.Same(MongoTSqlDialect.Instance, new MongoProvider().EditorLanguage);
    }

    [Fact]
    public void Mongo_provider_describes_endpoint_and_identity()
    {
        var profile = new ConnectionProfile { ProviderId = "mongodb" };
        profile.Values["host"] = "db.example.com";
        profile.Values["user"] = "larik";

        var provider = new MongoProvider();
        Assert.Equal("MongoDB · db.example.com:27017", provider.DescribeEndpoint(profile));
        Assert.Equal("larik", provider.DescribeIdentity(profile));
    }

    [Fact]
    public void Resolve_returns_the_mongo_dialect_for_the_provider()
    {
        Assert.Same(MongoTSqlDialect.Instance, EditorLanguageHelpers.Resolve(new MongoProvider()));
    }
}