using Larik.Data;
using Larik.Data.SqlServer;
using Xunit;

namespace Larik.Data.Tests;

/// <summary>
/// Golden tests for the editor-language seam. They encode the behaviour the app shipped before the
/// seam existed, so a porting mistake shows up as a diff rather than as a subtle editor regression.
/// The dialect under test is the real T-SQL one from the SQL Server provider assembly.
/// </summary>
public sealed class EditorLanguageTests
{
    private static readonly ISqlDialect Tsql = TSqlDialect.Instance;

    private static string[] Statements(IEditorLanguage language, string text) =>
        language.Split(text)
            .Select(span => text.Substring(span.Start, span.Length))
            .ToArray();

    private static string[] Words(IEditorLanguage language, string text) =>
        language.Tokenize(text)
            .Select(token => text.Substring(token.Start, token.Length))
            .ToArray();

    // ------------------------------------------------------------------ splitting

    [Fact]
    public void Splits_on_semicolon_and_go_and_keeps_the_terminator()
    {
        const string text = "SELECT 1;\nGO\nSELECT 2";
        var spans = Tsql.Split(text);

        Assert.Equal(2, spans.Count);
        Assert.Equal("SELECT 1;", text.Substring(spans[0].Start, spans[0].Length));
        Assert.Equal("SELECT 2", text.Substring(spans[1].Start, spans[1].Length));
    }

    [Fact]
    public void Accepts_a_repeat_count_after_go()
    {
        const string text = "SELECT 1\nGO 10\nSELECT 2";
        Assert.Equal(2, Tsql.Split(text).Count);
    }

    [Fact]
    public void An_indented_go_still_separates()
    {
        const string text = "SELECT 1\n  GO  \nSELECT 2";
        Assert.Equal(2, Tsql.Split(text).Count);
    }

    [Theory]
    [InlineData("SELECT 1 GO")]
    [InlineData("BEGIN\nSELECT GO\nEND")]
    public void Go_must_stand_alone_on_its_own_line(string text)
    {
        Assert.Single(Tsql.Split(text));
    }

    [Fact]
    public void Ignores_semicolons_inside_strings_comments_and_brackets()
    {
        Assert.Single(Tsql.Split("SELECT 'a;b';"));
        Assert.Single(Tsql.Split("SELECT 'it''s';"));
        Assert.Single(Tsql.Split("-- a;b\nSELECT 1"));
        Assert.Single(Tsql.Split("/* a;b */ SELECT 1"));
        Assert.Single(Tsql.Split("SELECT [a;b] FROM t"));
        Assert.Single(Tsql.Split("SELECT [a]]b] FROM t"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-- only\n/* comments */")]
    public void Nothing_runnable_means_no_statements(string text)
    {
        Assert.Empty(Tsql.Split(text));
        Assert.True(Tsql.StatementAt(text, text.Length).IsEmpty);
    }

    [Fact]
    public void A_statement_without_a_terminator_runs_to_the_end()
    {
        Assert.Equal(["SELECT 1"], Statements(Tsql, "SELECT 1"));
    }

    [Theory]
    [InlineData("SELECT 'unterminated")]
    [InlineData("SELECT [unterminated")]
    [InlineData("SELECT 1 /* unterminated")]
    public void Unterminated_constructs_run_to_the_end_without_splitting(string text)
    {
        Assert.Single(Tsql.Split(text));
    }

    // ------------------------------------------------------------------ batch splitting (F5)

    [Fact]
    public void Batches_split_only_on_go_and_keep_semicolons_inside_a_batch()
    {
        const string text = "SELECT 1; SELECT 2;\nGO\nSELECT 3";
        var spans = Tsql.SplitBatches(text);

        Assert.Equal(2, spans.Count);
        Assert.Equal("SELECT 1; SELECT 2;", text.Substring(spans[0].Start, spans[0].Length));
        Assert.Equal("SELECT 3", text.Substring(spans[1].Start, spans[1].Length));
    }

    [Fact]
    public void Batches_ignore_a_go_inside_strings_and_comments()
    {
        Assert.Single(Tsql.SplitBatches("SELECT 'GO;';\n-- GO\nSELECT 2"));
    }

    [Fact]
    public void Batches_are_empty_for_nothing_runnable()
    {
        Assert.Empty(Tsql.SplitBatches("-- GO\n/* GO */"));
        Assert.Empty(Tsql.SplitBatches(""));
    }

    [Fact]
    public void Batches_without_a_go_send_the_whole_document()
    {
        var spans = Tsql.SplitBatches("SELECT 1; SELECT 2;");
        Assert.Single(spans);
    }

    // ------------------------------------------------------------------ caret semantics

    [Fact]
    public void Caret_at_end_selects_the_last_statement()
    {
        const string text = "SELECT 1;\nSELECT 2;";
        var span = Tsql.StatementAt(text, text.Length);
        Assert.Equal("SELECT 2;", text.Substring(span.Start, span.Length));
    }

    [Fact]
    public void Caret_on_a_blank_line_between_statements_prefers_the_previous_one()
    {
        const string text = "SELECT 1;\n\nSELECT 2;";
        var caret = text.IndexOf("\n\n", StringComparison.Ordinal) + 1;
        var span = Tsql.StatementAt(text, caret);
        Assert.Equal("SELECT 1;", text.Substring(span.Start, span.Length));
    }

    [Fact]
    public void Caret_at_the_start_selects_the_first_statement()
    {
        const string text = "  SELECT 1;\nSELECT 2;";
        var span = Tsql.StatementAt(text, 0);
        Assert.Equal("SELECT 1;", text.Substring(span.Start, span.Length));
    }

    [Fact]
    public void Caret_at_a_statement_end_belongs_to_that_statement()
    {
        const string text = "SELECT 1;\nSELECT 2;";
        var first = Tsql.Split(text)[0];
        var span = Tsql.StatementAt(text, first.End);
        Assert.Equal("SELECT 1;", text.Substring(span.Start, span.Length));
    }

    // ------------------------------------------------------------------ tokenizing

    [Fact]
    public void Classifies_keywords_functions_variables_and_plain_identifiers()
    {
        var tokens = Tsql.Tokenize("SELECT COUNT (@id) FROM #temp WHERE #temp2(x) = 1");

        Assert.Contains(tokens, token => token is { Kind: QueryTokenKind.Keyword, Start: 0, Length: 6 });
        Assert.Contains(tokens, token => token is { Kind: QueryTokenKind.Function, Start: 7, Length: 5 });
        Assert.Contains(tokens, token => token is { Kind: QueryTokenKind.Variable, Start: 14, Length: 3 });
        Assert.Contains(tokens, token => token is { Kind: QueryTokenKind.Keyword, Start: 19, Length: 4 });
        // #temp is an identifier, not a keyword or a variable; #temp2( is the historical function quirk.
        Assert.Contains(tokens, token => token is { Kind: QueryTokenKind.Function, Start: 36, Length: 6 });
    }

    [Fact]
    public void String_tokens_include_the_unicode_prefix_and_doubled_quotes()
    {
        Assert.Equal(["N'x'"], Words(Tsql, "N'x'"));
        Assert.Equal(["'a''b'"], Words(Tsql, "'a''b'"));
        Assert.Equal(["\"q\""], Words(Tsql, "\"q\""));
    }

    [Theory]
    [InlineData("123", 3)]
    [InlineData("1.5", 3)]
    [InlineData("1e10", 4)]
    [InlineData("1e-5", 4)]
    [InlineData("1e+2", 4)]
    [InlineData("1.2.3", 5)]
    public void Number_tokens_match_the_historical_scanner(string text, int length)
    {
        var token = Assert.Single(Tsql.Tokenize(text));
        Assert.Equal(QueryTokenKind.Number, token.Kind);
        Assert.Equal(length, token.Length);
    }

    [Fact]
    public void Bracketed_identifiers_are_left_uncoloured()
    {
        Assert.Empty(Tsql.Tokenize("[Order Details]"));
        Assert.Empty(Tsql.Tokenize("[unterminated"));
    }

    [Fact]
    public void Comments_are_whole_tokens()
    {
        Assert.Equal(["-- x"], Words(Tsql, "-- x"));
        Assert.Equal(["/* x */"], Words(Tsql, "/* x */"));
        Assert.Equal(["-- x"], Words(Tsql, "-- x\ny"));
    }

    // ------------------------------------------------------------------ provider wiring

    [Fact]
    public void Resolve_falls_back_to_generic_sql_when_a_provider_has_no_language()
    {
        Assert.Same(GenericSqlLanguage.Default, EditorLanguageHelpers.Resolve(null));
        Assert.Same(GenericSqlLanguage.Default, EditorLanguageHelpers.Resolve(new NoLanguageProvider()));
    }

    [Fact]
    public void Sql_server_provider_exposes_the_tsql_dialect()
    {
        Assert.Same(TSqlDialect.Instance, new SqlServerProvider().EditorLanguage);
    }

    [Fact]
    public void Tsql_quotes_identifiers_the_way_sql_server_does()
    {
        Assert.Equal("[Order Details]", Tsql.QuoteIdentifier("Order Details"));
        Assert.Equal("[a]]b]", Tsql.QuoteIdentifier("a]b"));
    }

    [Fact]
    public void Tsql_completion_metadata_carries_the_old_lists()
    {
        Assert.Contains("SELECT", Tsql.Keywords);
        Assert.Contains("INNER JOIN", Tsql.Keywords);
        Assert.Contains("COUNT", Tsql.Functions);
        Assert.Contains("FROM", Tsql.TableContextKeywords);
    }

    [Fact]
    public void Generic_and_tsql_scan_the_same_corpus_identically()
    {
        string[] corpus =
        [
            "SELECT TOP 10 [name] FROM dbo.users WHERE id = @id;",
            "GO\nSELECT N'x;y' -- trailing\nGO",
            "/* block; comment */ SELECT COUNT(*) FROM #temp WHERE a = 1.5e-3",
            "insert into t (a, b) values ('it''s', 2);\nselect * from t",
        ];

        foreach (var text in corpus)
        {
            var expectedTokens = Tsql.Tokenize(text);
            var actualTokens = GenericSqlLanguage.Default.Tokenize(text);
            Assert.Equal(expectedTokens, actualTokens);

            var expectedSpans = Tsql.Split(text);
            var actualSpans = GenericSqlLanguage.Default.Split(text);
            Assert.Equal(expectedSpans, actualSpans);

            var expectedBatches = Tsql.SplitBatches(text);
            var actualBatches = GenericSqlLanguage.Default.SplitBatches(text);
            Assert.Equal(expectedBatches, actualBatches);
        }
    }

    [Fact]
    public void Statement_spans_are_not_tagged_for_sql()
    {
        Assert.All(Tsql.Split("SELECT 1; SELECT 2"), span => Assert.Null(span.Language));
    }

    // ------------------------------------------------------------------ contract compatibility

    [Fact]
    public void The_four_argument_foreign_key_constructor_still_works()
    {
        var key = new ForeignKeySchema("fk", "customer_id", "customers", "id");
        Assert.Null(key.ReferencedSchema);
        Assert.Equal(key, new ForeignKeySchema("fk", "customer_id", "customers", "id"));
    }

    [Fact]
    public void New_result_and_schema_properties_default_to_null()
    {
        var result = new QueryResult
        {
            Columns = [],
            Rows = [],
            Message = "ok",
        };
        Assert.Null(result.GeneratedQuery);

        var table = new TableSchema { Name = "users", Columns = ["id"] };
        Assert.Null(table.Schema);
    }

    private sealed class NoLanguageProvider : IDatabaseProvider
    {
        public string Id => "none";

        public string DisplayName => "None";

        public string Dialect => "none";

        public IReadOnlyList<ProviderSetting> Settings => [];

        public IDatabaseConnection CreateConnection(ConnectionProfile profile) =>
            throw new NotSupportedException();
    }
}
