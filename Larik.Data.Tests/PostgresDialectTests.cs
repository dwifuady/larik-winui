using Larik.Data;
using Larik.Data.Postgres;
using Xunit;

namespace Larik.Data.Tests;

/// <summary>
/// Golden tests for the PostgreSQL dialect and provider wiring. They cover the constructs that make
/// PostgreSQL different from T-SQL (dollar quotes, escape strings, quoted identifiers, LIMIT and CALL)
/// without needing a server; execution, schema mapping and transactions live in the UI suite.
/// </summary>
public sealed class PostgresDialectTests
{
    private static readonly ISqlDialect Pg = PostgresDialect.Instance;

    private static string[] Words(IEditorLanguage language, string text) =>
        language.Tokenize(text)
            .Select(token => text.Substring(token.Start, token.Length))
            .ToArray();

    // ------------------------------------------------------------------ splitting

    [Fact]
    public void Splits_on_semicolon_and_keeps_the_terminator()
    {
        const string text = "SELECT 1;\nSELECT 2";
        var spans = Pg.Split(text);

        Assert.Equal(2, spans.Count);
        Assert.Equal("SELECT 1;", text.Substring(spans[0].Start, spans[0].Length));
        Assert.Equal("SELECT 2", text.Substring(spans[1].Start, spans[1].Length));
    }

    [Fact]
    public void Go_is_ordinary_text_in_postgresql()
    {
        // GO is an SSMS separator, not PostgreSQL syntax (and not a batch separator here).
        const string text = "SELECT 1;\nGO\nSELECT 2;";
        var spans = Pg.Split(text);

        Assert.Equal(2, spans.Count);
        Assert.Equal("SELECT 1;", text.Substring(spans[0].Start, spans[0].Length));
        Assert.Equal("GO\nSELECT 2;", text.Substring(spans[1].Start, spans[1].Length));
        Assert.Single(Pg.SplitBatches(text));
    }

    [Fact]
    public void Dollar_quotes_hide_semicolons_and_keep_their_body_in_one_statement()
    {
        const string text = "DO $$ BEGIN PERFORM 1; PERFORM 2; END $$;\nSELECT 3;";
        var spans = Pg.Split(text);

        Assert.Equal(2, spans.Count);
        Assert.Equal("DO $$ BEGIN PERFORM 1; PERFORM 2; END $$;", text.Substring(spans[0].Start, spans[0].Length));
        Assert.Equal("SELECT 3;", text.Substring(spans[1].Start, spans[1].Length));
    }

    [Fact]
    public void Tagged_dollar_quotes_must_match_their_tag()
    {
        const string text = "CREATE FUNCTION f() RETURNS void AS $body$ SELECT 1; $other$ SELECT 2; $body$ LANGUAGE sql;";
        Assert.Single(Pg.Split(text));
    }

    [Fact]
    public void Dollar_quotes_survive_unterminated_input()
    {
        Assert.Single(Pg.Split("SELECT $$a;b"));
    }

    [Fact]
    public void Escape_strings_and_quoted_identifiers_hide_semicolons()
    {
        Assert.Single(Pg.Split(@"SELECT E'it\'s;fine';"));
        Assert.Single(Pg.Split("SELECT \"a;b\" FROM t;"));
        Assert.Single(Pg.Split("SELECT \"a\"\"b;c\" FROM t;"));
        Assert.Single(Pg.Split("-- a;b\nSELECT 1"));
        Assert.Single(Pg.Split("/* a;b */ SELECT 1"));
    }

    [Fact]
    public void Nested_block_comments_do_not_end_at_the_first_close()
    {
        const string text = "/* outer /* inner; */ still comment; */ SELECT 1;";
        Assert.Single(Pg.Split(text));

        var comment = Assert.Single(Pg.Tokenize(text), token => token.Kind == QueryTokenKind.Comment);
        Assert.Equal("/* outer /* inner; */ still comment; */", text.Substring(comment.Start, comment.Length));
    }

    [Fact]
    public void An_identifier_with_a_dollar_sign_is_not_a_dollar_quote()
    {
        const string text = "SELECT a$b$c FROM t;";
        Assert.Single(Pg.Split(text));
        Assert.DoesNotContain(Pg.Tokenize(text), token => token.Kind == QueryTokenKind.String);
    }

    [Fact]
    public void Positional_parameter_markers_are_not_dollar_quotes()
    {
        Assert.Single(Pg.Split("SELECT $1;"));
        Assert.Equal(["SELECT", "1"], Words(Pg, "SELECT $1;"));
    }

    // ------------------------------------------------------------------ batch splitting

    [Fact]
    public void Batches_are_the_whole_document_because_postgresql_has_no_batch_separator()
    {
        var text = "SELECT 1; SELECT 2;";
        var span = Assert.Single(Pg.SplitBatches(text));
        Assert.Equal(text, text.Substring(span.Start, span.Length));
    }

    [Fact]
    public void Batches_are_empty_for_nothing_runnable()
    {
        Assert.Empty(Pg.SplitBatches("-- only\n/* comments */"));
        Assert.Empty(Pg.SplitBatches(""));
    }

    // ------------------------------------------------------------------ caret semantics

    [Fact]
    public void Caret_at_end_selects_the_last_statement()
    {
        const string text = "SELECT 1;\nSELECT 2;";
        var span = Pg.StatementAt(text, text.Length);
        Assert.Equal("SELECT 2;", text.Substring(span.Start, span.Length));
    }

    // ------------------------------------------------------------------ tokenizing

    [Fact]
    public void Classifies_keywords_and_function_calls()
    {
        var tokens = Pg.Tokenize("SELECT COUNT(*) FROM users WHERE id = 1");

        Assert.Contains(tokens, token => token is { Kind: QueryTokenKind.Keyword, Start: 0, Length: 6 });
        Assert.Contains(tokens, token => token is { Kind: QueryTokenKind.Function, Start: 7, Length: 5 });
        Assert.Contains(tokens, token => token is { Kind: QueryTokenKind.Number });
    }

    [Fact]
    public void Dollar_quotes_escape_strings_and_double_quotes_are_classified_as_expected()
    {
        Assert.Equal(["$$a;b$$"], Words(Pg, "$$a;b$$"));
        Assert.Equal(["$tag$ x $tag$"], Words(Pg, "$tag$ x $tag$"));
        Assert.Equal([@"E'it\'s'"], Words(Pg, @"E'it\'s'"));
        // A quoted identifier is an identifier: skipped, so it keeps the default colour.
        Assert.Empty(Pg.Tokenize("\"quoted name\""));
    }

    [Fact]
    public void N_prefix_is_not_a_postgresql_string_prefix()
    {
        var tokens = Pg.Tokenize("N'x'");
        Assert.DoesNotContain(tokens, token => token.Kind == QueryTokenKind.String && token.Start == 0);
    }

    // ------------------------------------------------------------------ provider wiring

    [Fact]
    public void Provider_exposes_the_postgresql_dialect()
    {
        var provider = new PostgresProvider();

        Assert.Equal("postgres", provider.Id);
        Assert.Equal("postgres", provider.Dialect);
        Assert.Same(PostgresDialect.Instance, provider.EditorLanguage);
    }

    [Fact]
    public void Provider_declares_the_postgresql_fields_and_keeps_the_password_secret()
    {
        var provider = new PostgresProvider();

        Assert.Contains(provider.Settings, setting => setting.Key == "host" && setting.Required);
        Assert.Contains(provider.Settings, setting => setting.Key == "port" && setting.Kind == ProviderSettingKind.Number);
        Assert.Contains(provider.Settings, setting => setting.Key == "database");
        Assert.Contains(provider.Settings, setting => setting.Key == "user");
        Assert.Contains(provider.Settings, setting => setting.Key == "password" && setting.Kind == ProviderSettingKind.Password);
        Assert.Contains(provider.Settings, setting => setting.Key == "sslMode" && setting.IsAdvanced);
    }

    [Fact]
    public void Ssl_mode_default_is_resolved_from_the_host_instead_of_written_down()
    {
        var setting = Assert.Single(
            new PostgresProvider().Settings,
            item => item.Key == "sslMode");

        // Empty means "Prefer for localhost, VerifyFull for remote"; a stored value would freeze
        // the old Prefer default into every new profile.
        Assert.Null(setting.DefaultValue);
        Assert.Contains("VerifyFull", setting.Placeholder, StringComparison.Ordinal);
    }

    [Fact]
    public void Warns_only_when_a_remote_connection_can_skip_certificate_verification()
    {
        var provider = new PostgresProvider();

        var remotePrefer = new ConnectionProfile { ProviderId = "postgres" };
        remotePrefer.Values["host"] = "db.internal";
        remotePrefer.Values["sslMode"] = "Prefer";
        Assert.NotNull(provider.DescribeSecurityWarning(remotePrefer));

        var remoteDefault = new ConnectionProfile { ProviderId = "postgres" };
        remoteDefault.Values["host"] = "db.internal";
        Assert.Null(provider.DescribeSecurityWarning(remoteDefault));

        var localPrefer = new ConnectionProfile { ProviderId = "postgres" };
        localPrefer.Values["host"] = "localhost";
        localPrefer.Values["sslMode"] = "Prefer";
        Assert.Null(provider.DescribeSecurityWarning(localPrefer));
    }

    [Fact]
    public void Provider_describes_its_endpoint_without_the_shell_knowing_the_provider()
    {
        var profile = new ConnectionProfile { ProviderId = "postgres" };
        profile.Values["host"] = "db.internal";
        profile.Values["port"] = "5433";

        Assert.Equal("PostgreSQL · db.internal:5433", new PostgresProvider().DescribeEndpoint(profile));
    }

    [Fact]
    public async Task A_new_session_reports_no_transaction_and_no_database_before_it_opens()
    {
        var provider = new PostgresProvider();
        var profile = new ConnectionProfile { ProviderId = provider.Id };
        await using var session = provider.CreateConnection(profile);

        Assert.True(session.SupportsTransactions);
        Assert.Equal(ConnectionState.Disconnected, session.State);
        Assert.Equal(SessionTransactionState.None, await session.GetTransactionStateAsync());
        Assert.Null(await session.GetCurrentDatabaseAsync());
    }

    // ------------------------------------------------------------------ quoting and completion metadata

    [Fact]
    public void Quotes_identifiers_the_way_postgresql_does()
    {
        Assert.Equal("\"Order Details\"", Pg.QuoteIdentifier("Order Details"));
        Assert.Equal("\"a\"\"b\"", Pg.QuoteIdentifier("a\"b"));
    }

    [Fact]
    public void Completion_metadata_carries_postgresql_vocabulary()
    {
        Assert.Contains("SELECT", Pg.Keywords);
        Assert.Contains("RETURNING", Pg.Keywords);
        Assert.Contains("LIMIT", Pg.Keywords);
        Assert.Contains("GENERATE_SERIES", Pg.Functions);
        Assert.Contains("FROM", Pg.TableContextKeywords);
        Assert.Equal(["CALL"], Pg.RoutineContextKeywords);
    }

    [Fact]
    public void Expands_a_procedure_call_with_positional_null_arguments()
    {
        var parameters = new[]
        {
            new RoutineParameter { Name = "p_schema", DataType = "text" },
            new RoutineParameter { Name = "p_count", DataType = "integer" },
        };

        var call = Pg.RoutineCall("public", "refresh_stats", parameters);

        Assert.Equal("\"public\".\"refresh_stats\"(NULL, NULL)", call.Text);
        Assert.True(call.HasSelection);
        Assert.Equal("NULL", call.Text.Substring(call.SelectionStart, call.SelectionLength));
    }

    [Fact]
    public void A_routine_without_parameters_gets_empty_parentheses()
    {
        var call = Pg.RoutineCall("public", "refresh_stats", []);
        Assert.Equal("\"public\".\"refresh_stats\"()", call.Text);
        Assert.False(call.HasSelection);
    }

    [Fact]
    public void Invocation_adds_call_for_a_procedure()
    {
        var invocation = Pg.RoutineInvocation("public", "p", [new RoutineParameter { Name = "p_id" }]);
        Assert.Equal("CALL \"public\".\"p\"(NULL)", invocation);
    }

    [Fact]
    public void Invocation_selects_a_function_because_call_only_accepts_procedures()
    {
        var invocation = Pg.RoutineInvocation(
            "public",
            "add_one",
            [new RoutineParameter { Name = "x", DataType = "integer" }],
            RoutineKind.Function);

        Assert.Equal("SELECT \"public\".\"add_one\"(NULL)", invocation);
    }

    [Fact]
    public void Preview_uses_limit_and_quotes_the_object()
    {
        Assert.Equal(
            "SELECT * FROM \"public\".\"orders\" LIMIT 100",
            Pg.PreviewStatement("public", "orders", 100));
    }

    [Fact]
    public void Reference_lookup_uses_limit_and_a_typed_filter()
    {
        var sql = Pg.RowsStatement(
            "public",
            "orders",
            [new RowFilter("id", "1", "integer")],
            orderByColumn: null,
            limit: 25);

        Assert.StartsWith("SELECT * FROM \"public\".\"orders\"", sql);
        Assert.Contains("WHERE \"id\" = 1", sql);
        Assert.EndsWith("LIMIT 25", sql);
    }

    [Fact]
    public void Formats_literals_for_the_reference_filter()
    {
        Assert.Equal("E'it''s'", Pg.FormatLiteral("it's", "text"));
        Assert.Equal("42", Pg.FormatLiteral("42", "integer"));
        Assert.Equal("TRUE", Pg.FormatLiteral("true", "boolean"));
        Assert.Equal("E'42'", Pg.FormatLiteral("42", "text"));
        Assert.Equal("NULL", Pg.FormatLiteral(null, "integer"));
    }

    [Fact]
    public void Backslashes_cannot_close_a_literal_when_standard_conforming_strings_is_off()
    {
        // The E'' prefix makes the escape rules explicit; both characters are escaped, so the
        // trailing text stays inside the literal instead of becoming a second statement.
        Assert.Equal(
            @"E'\\''; DROP TABLE x; --'",
            Pg.FormatLiteral(@"\'; DROP TABLE x; --", "text"));
    }

    [Fact]
    public void An_empty_ssl_mode_verifies_remote_hosts_and_stays_light_for_localhost()
    {
        var provider = new PostgresProvider();

        // A stored "Prefer" is an explicit choice and is left alone.
        Assert.Equal(Npgsql.SslMode.Prefer, PostgresProvider.ParseSslMode("Prefer", isLocalHost: false));
        Assert.Equal(Npgsql.SslMode.VerifyFull, PostgresProvider.ParseSslMode(null, isLocalHost: false));
        Assert.Equal(Npgsql.SslMode.Prefer, PostgresProvider.ParseSslMode("", isLocalHost: true));
        Assert.Equal(Npgsql.SslMode.VerifyFull, PostgresProvider.ParseSslMode("", isLocalHost: false));
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("[::1]", true)]
    [InlineData("/var/run/postgresql", true)]
    [InlineData("db.internal", false)]
    [InlineData("10.0.0.5", false)]
    public void Loopback_hosts_are_treated_as_local(string host, bool expected)
    {
        Assert.Equal(expected, PostgresProvider.IsLocalHost(host));
    }

    [Fact]
    public void Bytea_keeps_its_hex_prefix_inside_an_escape_string()
    {
        Assert.Equal(@"E'\\xDEADBEEF'", Pg.FormatLiteral("0xDEADBEEF", "bytea"));
    }
}
