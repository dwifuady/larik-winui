# Adding a database provider

This is the handbook for extending Larik with another database engine. Read
[`../AGENTS.md`](../AGENTS.md) first for the repository rules.

A provider is an assembly that implements `Larik.Data.IDatabaseProvider`. It owns two things:

1. **The data plane** — connections, execution, result sets, database list, schema metadata.
2. **The editor language** — tokenizing, statement splitting, and (for SQL) completion metadata.

The app knows neither. It renders settings from the provider, talks to `IDatabaseConnection`, and
asks `IDatabaseProvider.EditorLanguage` how to paint and split text.

---

## 1. Contract map

All contracts live in `Larik.Data` (`net10.0`, zero package references, no WinUI types).

| Type | Purpose |
|---|---|
| `IDatabaseProvider` | Identity (`Id`, `DisplayName`, `Dialect`), settings, `CreateConnection`, `EditorLanguage`, `DescribeEndpoint`, `DescribeIdentity`. |
| `IDatabaseConnection` | One session: `OpenAsync`/`CloseAsync`, `State`, `ServerVersion`, `GetDatabasesAsync`, `GetDatabaseDetailsAsync` (optional; marks databases the login cannot open), `ExecuteAsync`, `ExecuteBatchAsync`, `GetSchemaAsync`, `GetRoutinesAsync` (optional; procedures), `EstimateRowCountAsync` (optional; reference-preview size hint), `GetCurrentDatabaseAsync` (optional; reports a `USE` to the shell), plus optional transaction state/commit/rollback. |
| `ConnectionProfile` | Saved non-secret values keyed by `ProviderSetting.Key`; `Get`/`GetInt`/`GetBool`. |
| `ProviderSetting` | Declarative connection-dialog field (`Text`, `Password`, `Number`, `Boolean`, `IsAdvanced`). |
| `QueryResult` | Columns, rows, `RecordsAffected`, message, error flag, elapsed, row cap, `Messages` (PRINT/warnings), `GeneratedQuery`, and `Statement` (the SQL that produced the result, used to resolve references). |
| `TableSchema` / `ForeignKeySchema` | Table or view name, optional `Schema`, `Kind` (`Table`/`View`), columns, `ColumnDetails` (types), primary key, foreign keys (with `ReferencedSchema`). |
| `RoutineSchema` / `RoutineParameter` | Stored procedure or function: optional `Schema`, `Kind`, ordered parameters with type/output/default. |
| `QueryExecutionOptions` | `MaxRows` with a 5,000 default / 100,000 ceiling. |
| `SessionTransactionState` | `TransactionCount`, `XactState`, `HasTransaction`, `IsDoomed`, `CanCommit`, `CanRollback`. |
| `IEditorLanguage` | `Id`, `Tokenize`, `Split`, `SplitBatches`, `StatementAt`. Non-SQL engines implement this. |
| `ISqlDialect` | `IEditorLanguage` plus `Keywords`, `Functions`, `TableContextKeywords`, `RoutineContextKeywords`, `RoutineCall`, `RoutineInvocation`, `PreviewStatement`, `RowsStatement`, `FormatLiteral`, `QuoteIdentifier`. |
| `SqlLanguage` / `SqlLanguageOptions` | Reusable SQL lexer/splitter; provider assemblies subclass it. |

`IDatabaseConnection` already uses default interface members for the row-cap overload,
`ExecuteBatchAsync` and the transaction members, so providers written against an older contract keep
compiling and running.

**A connection is a session, not a shared server handle.** `ConnectionViewModel.CreateSession()` is
called once per query tab; the workspace keeps only a control session for connection state and the
database list. Providers must keep per-tab state (temp tables, `SET` options, an open transaction) on
the `IDatabaseConnection` instance they return.

### A minimal provider

```csharp
using Larik.Data;

public sealed class MyProvider : IDatabaseProvider
{
    public string Id => "mysqldb";
    public string DisplayName => "MySQL";
    public string Dialect => "mysqldb";             // snippet-scoping id
    public IEditorLanguage? EditorLanguage => MySqlDialect.Instance;

    public IReadOnlyList<ProviderSetting> Settings { get; } =
    [
        new() { Key = "host", Label = "Host", Required = true, DefaultValue = "localhost" },
        new() { Key = "port", Label = "Port", Kind = ProviderSettingKind.Number, DefaultValue = "3306", IsAdvanced = true },
        new() { Key = "user", Label = "User" },
        new() { Key = "password", Label = "Password", Kind = ProviderSettingKind.Password },
    ];

    public IDatabaseConnection CreateConnection(ConnectionProfile profile) => new MyConnection(profile);
}
```

A provider can also describe the saved login for the Workspaces page:

```csharp
public string? DescribeIdentity(ConnectionProfile profile) =>
    string.IsNullOrWhiteSpace(profile.Get("user")) ? null : profile.Get("user");
```

The default returns null, so an older provider simply shows no identity.

The connection dialog builds itself from `Settings`. Password fields are stored in Windows
Credential Manager, never in the SQLite state file. `IsAdvanced` moves rare fields into the
dialog's **Advanced** section.

---

## 2. Contract compatibility (plugins depend on it)

The catalogue loads provider DLLs that may have been compiled against an older `Larik.Data`.
Therefore:

1. **Add-only interfaces.** A new member needs a default implementation. Example:
   `IEditorLanguage? EditorLanguage => null;`
2. **Never rename or remove a member.** Removing one produces `MissingMethodException` at call time
   in an old plugin.
3. **Never add positional parameters to contract records.** Add a nullable `init` property inside
   the record body instead (`ForeignKeySchema.ReferencedSchema` follows this rule; the four-argument
   constructor is unchanged).
4. **New metadata is optional and nullable.** `QueryResult.GeneratedQuery`, `TableSchema.Schema` and
   `ForeignKeySchema.ReferencedSchema` are all null for existing providers; `QueryResult.Statement`,
   `TableSchema.Kind` defaults to `Table`, `ColumnDetails`/`Routines`/`Parameters` default to empty,
   `GetRoutinesAsync` returns `[]`, `EstimateRowCountAsync` returns null, `DescribeEndpoint` returns
   null (the rail falls back to `DisplayName`), and the dialect's
   `RowsStatement`/`FormatLiteral` have defaults, so an older provider behaves exactly as before and
   nothing is persisted in a new format.
5. **Prefer string tags over contract enums** for open sets. `StatementSpan.Language` is a string so
   a future MongoDB language can tag spans `"shell"`, `"line"`, `"sql"` without a contract change.

`Larik.Data.Tests/EditorLanguageTests.cs` contains compatibility guards for the record constructor
and the new default-null properties. Keep them.

---

## 3. The editor-language seam

### Which interface?

- **SQL engine** → `ISqlDialect` + `SqlLanguage`. Reuse the shared scanner.
- **Non-SQL engine** → `IEditorLanguage` only. Implement your own tokenizer/splitter; do
  **not** pretend it is a SQL dialect. (MongoDB is the one exception already shipped: its document
  surface is the user's T-SQL, so it implements `ISqlDialect` — see §9. The rule stands for a surface
  that is genuinely non-SQL.)

`ISqlDialect` exists because completion needs a few lists (keywords, functions, table-context
words). Everything else about the editor is plain `IEditorLanguage`.

### How the app uses it

```
ConnectionViewModel.EditorLanguage  ->  EditorLanguageHelpers.Resolve(Provider)
StudioViewModel.ActiveLanguage      ->  active space's connection, else GenericSqlLanguage.Default
MainPage                            ->  SqlEditor.EditorLanguage = ViewModel.ActiveLanguage
SqlEditorView.ApplyHighlighting     ->  EditorLanguage.Tokenize(text)
SqlEditorView.UpdateCompletions     ->  new CompletionQuery(text, caret, EditorLanguage)
MainPage.RunFromEditorAsync         ->  SqlEditor.EditorLanguage.StatementAt(text, caret)
StudioCompletionSource              ->  query.Language as ISqlDialect -> CompletionRequest.Dialect
```

When `EditorLanguage` is null, `EditorLanguageHelpers.Resolve` returns `GenericSqlLanguage.Default`.
That fallback is configured to behave exactly like the app's historical lexer/splitter (brackets,
`N'...'`, `@` variables, `GO`), so Demo and unknown providers are unchanged.

`ActiveLanguage` is notified in `StudioViewModel` wherever `Snippets` is notified (space switch,
connection reconfiguration, database switch). `ConnectionViewModel.ReconfigureAsync` also raises
`EditorLanguage`.

### `IEditorLanguage` semantics

```csharp
public interface IEditorLanguage
{
    string Id { get; }
    IReadOnlyList<QueryToken> Tokenize(string? text);
    IReadOnlyList<StatementSpan> Split(string? text);
    IReadOnlyList<StatementSpan> SplitBatches(string? text); // F5 unit
    StatementSpan StatementAt(string? text, int caretOffset); // default implementation
}
```

- `Tokenize` is called on a 140 ms typing debounce. Single pass, synchronous, no I/O, no reflection.
- `QueryTokenKind` is `Comment`, `String`, `Number`, `Keyword`, `Function`, `Variable`. The app maps
  kinds to `StudioSyntax*Brush` theme resources; providers only classify.
- `Split` returns spans of statements with runnable content. Spans **include their trailing
  terminator** (`SELECT 1;` includes `;`). Whitespace-only and comment-only spans are not
  statements.
- `StatementAt` is the Ctrl+Enter contract: the caret in whitespace after a statement belongs to
  that statement. `SqlLanguage` overrides the default with a comment-aware implementation.
- `SplitBatches` is the F5 contract: split only on the engine's batch separator (T-SQL `GO`) and
  keep semicolons inside a batch, because the server executes a batch as one command. A language
  without a separator sends the trimmed document as one batch. T-SQL also accepts an indented `GO`;
  `GO n` repeat counts are parsed but not expanded.
- `StatementSpan.Language` is reserved for mixed-script documents (MongoDB). SQL languages leave it
  null.

### Using `SqlLanguage`

Subclass it and supply only the differences:

```csharp
public sealed class PostgresDialect : SqlLanguage, ISqlDialect
{
    public static PostgresDialect Instance { get; } = new();

    private PostgresDialect() : base(new SqlLanguageOptions
    {
        TokenKeywords = PgTokenKeywords,
        BracketIdentifiers = false,        // "x" identifiers, not [x]
        UnicodeStringPrefix = false,       // no N'...'
        IdentifierStartCharacters = [],
        IdentifierPartCharacters = ['$'],
        VariablePrefixes = [],             // no @variables
        StringQuotes = ['\''],
        LineCommentPrefixes = ["--"],
        BlockCommentOpen = "/*",
        BlockCommentClose = "*/",
        BatchSeparator = null,             // no GO
        SplitOnSemicolon = true,
    }) { }

    public override string Id => "postgres";
    public IReadOnlyList<string> Keywords => ...;
    public IReadOnlyList<string> Functions => ...;
    public IReadOnlyList<string> TableContextKeywords => ...;
    public string QuoteIdentifier(string name) => $"\"{name.Replace("\"", "\"\"")}\"";
}
```

`TSqlDialect` in `Larik.Data.SqlServer` is the reference implementation.

Not yet configurable, and deliberately deferred: `::` casts and `$1` parameters. Dollar-quoted
strings (`$$...$$`, `$tag$...$tag$`), `E'...'` strings, double-quoted identifiers and nested block
comments are now options (`DollarQuotedStrings`, `EscapeStringPrefix`, `QuotedIdentifierQuotes`,
`NestedBlockComments`); extend `SqlLanguageOptions` additively rather than forking the scanner when
another engine needs more.

### Statement splitting: editor vs execution

These are two different jobs:

- **Editor splitting** (`IEditorLanguage.Split`/`StatementAt`) decides what Ctrl+Enter selects.
  `SplitBatches` decides what F5 sends as one server command. Both must be provider-owned and
  mixed-language capable. T-SQL splits statements on `;`, batches only on `GO`.
- **Execution splitting** happens inside `ExecuteBatchAsync`. SQL Server sends the batch natively
  and reads `NextResultAsync`; Demo splits on `;`; MongoDB would classify each statement.

Do not use one implementation for both unless the engine genuinely behaves the same. Do not
reintroduce a global SQL splitter in `Larik`.

---

## 4. Sessions and transactions

The shell creates one `IDatabaseConnection` per query tab (`ConnectionViewModel.CreateSession()`),
opens it lazily, and keeps it for the tab's lifetime. The workspace's own connection is only the
control session used for connection state and the database list. Consequences:

- A transaction, temp table, `SET` option or isolation level belongs to the tab that issued it.
- F5 sends one `ExecuteBatchAsync` call per editor batch on the tab's session, so a `GO` boundary
  starts a new server batch while the session — and its `USE`, temp tables and transaction — carries
  across.
- The shell refuses to switch database, change the connection profile or disconnect while a tab has
  an open transaction; closing a tab prompts to roll it back (and stops a running query first).
- `QuerySession` refreshes the transaction state after every batch in a `finally`, even when the
  batch failed or was cancelled, so the guard never reflects stale server state.

### Transaction contract

```csharp
bool SupportsTransactions => false;

Task<SessionTransactionState> GetTransactionStateAsync(CancellationToken ct = default) =>
    Task.FromResult(SessionTransactionState.None);

Task CommitTransactionAsync(CancellationToken ct = default) =>
    throw new NotSupportedException("This provider does not support transactions.");

Task RollbackTransactionAsync(CancellationToken ct = default) =>
    throw new NotSupportedException("This provider does not support transactions.");
```

- Leaving `SupportsTransactions` false (the default) is correct for engines without interactive
  transactions. The shell hides the guard, and `BEGIN`/`COMMIT` are then ordinary SQL text.
- When true, return the session's current state from `GetTransactionStateAsync`:
  `TransactionCount > 0` means open, `XactState == 1` means committable, `XactState < 0` means
  doomed/aborted and rollback-only. SQL Server reads `@@TRANCOUNT` and `XACT_STATE()`.
- PostgreSQL has no nested transactions: map `BEGIN` to count 1, an aborted transaction to
  `XactState = -1`, and (optionally) detect `idle in transaction` via `pg_stat_activity`. Savepoints
  remain SQL text; the shell only offers a whole-transaction commit/rollback. The shipped provider
  tracks `BEGIN`/`START TRANSACTION`, `COMMIT`/`END`, `ROLLBACK`/`ABORT`, `ROLLBACK TO SAVEPOINT`
  and `PREPARE TRANSACTION`, because the session's own `pg_stat_activity.state` is `active` while
  the probe runs; a failed statement inside the transaction marks it aborted, and a successful
  `ROLLBACK TO SAVEPOINT` keeps the transaction open and clears its aborted state.
- MongoDB can opt in later with the same members if multi-document transactions are supported; for
  a first plugin, leaving the defaults is fine.
- The transaction guard is per tab, not global. Never share one physical connection between
  sessions unless the engine's transaction state is genuinely per connection and you can map it.

---

## 5. Schema metadata

```csharp
new TableSchema
{
    Name = "users",
    Schema = "public",                 // optional; null for engines without schemas
    Kind = TableKind.View,             // optional; defaults to Table
    Columns = [...],
    ColumnDetails =                    // optional; names alone still work
    [
        new TableColumn { Name = "id", DataType = "bigint", IsNullable = false },
    ],
    PrimaryKey = [...],
    ForeignKeys =
    [
        new ForeignKeySchema("orders_user_id_fkey", "user_id", "users", "id")
        {
            ReferencedSchema = "public", // optional
        },
    ],
};

new RoutineSchema
{
    Name = "refresh_stats",
    Schema = "public",
    Kind = RoutineKind.Procedure,
    Parameters =
    [
        new RoutineParameter { Name = "p_schema", DataType = "text" },
    ],
};
```

Completion matches a reference either bare or schema-qualified (`[dbo].[users]`, `public.users`)
against `Schema` + `Name`, and inserts a schema-qualified name in the dialect's quoting
(`[dbo].[users] AS [u]` for T-SQL). A schema typed before the dot (`FROM public.`) narrows the list
and is not repeated in the insert. A provider that reports no `Schema` keeps the bare name, so this
is additive for engines without schemas. Views use the same `TableSchema` shape with `Kind = View`;
completion and the browser treat them like read-only tables.

The shell's schema browser lists tables, views and routines from these two calls. It draws no
engine-specific icons or columns: `Kind` chooses the view icon, `ColumnDetails` fills the type
column, and `RoutineSchema.Parameters` fills a procedure's detail. Return `[]` from
`GetRoutinesAsync` (the default) when the engine has no routines or the login cannot read them.

Routine syntax belongs to the dialect. After a `RoutineContextKeywords` word (`EXEC` for T-SQL,
`CALL` for PostgreSQL), completion offers procedures and calls `ISqlDialect.RoutineCall`, which
returns the insert text plus the span of the first argument value to select. The default renders
only the quoted name; T-SQL overrides it with named arguments
(`[dbo].[p] @a = NULL, @b = NULL`), and PostgreSQL renders positional `NULL`s.
`RoutineInvocation` prefixes the first keyword for the schema browser's Enter action; its
kind-aware overload receives `RoutineKind`, so PostgreSQL inserts `CALL "p"(...)` for a procedure
and `SELECT "f"(...)` for a function (CALL only accepts procedures).
`PreviewStatement` is the third dialect hook: the shell asks for a table or view's first rows
instead of hard-coding `SELECT TOP`, so PostgreSQL can answer with `LIMIT` and a provider without a
row-limit clause falls back to plain `SELECT`.

### Foreign-key reference previews

`QueryResult.Statement` lets the shell map an output column back to its source table (the resolver
uses the same lexer as completion, so aliased/ambiguous columns are deliberately skipped). It then
looks up `TableSchema.ForeignKeys` and asks the dialect for the rows:

```csharp
dialect.RowsStatement(
    targetSchema,
    targetTable,
    filters,                 // RowFilter(column, formattedValue, sourceDataType)
    orderByColumn,           // key column for full-list mode
    limit);
```

`FormatLiteral` turns the already-formatted cell text into an engine literal (T-SQL keeps numeric,
bit and `0x` values bare and escapes strings). `EstimateRowCountAsync` is the optional size hint:
a target at or below the list limit is shown in full with matching rows highlighted; a larger one
is fetched with the available key filters only. Returning null (the default) means "unknown" and
also uses the filtered lookup.

---

## 6. Result details

- Stop reading at `QueryExecutionOptions.EffectiveMaxRows + 1`; set `IsTruncated` and `RowLimit`.
  The default is 5,000 rows; the ceiling is 100,000.
- `ExecuteBatchAsync` returns one `QueryResult` per result set. The shell calls it once per editor
  batch (see §4), so a provider must keep session state on the connection instance rather than
  assuming a fresh connection per call.
- Return one result per statement even if only one has columns.
- `QueryResult.Error` is the shape for a statement error (the grid shows the message and timing).
- `QueryResult.Messages` is for output that is neither rows nor an error: `PRINT`, `RAISERROR` up
  to severity 10, `SET STATISTICS IO/TIME`, optimizer warnings, and `USE`'s database-context line.
  The SQL Server provider fills it from `SqlConnection.InfoMessage` (one Messages tab per batch);
  other providers leave it empty and the tab never appears.
- `QueryResult.GeneratedQuery` is for engines that translate user text. Example: a MongoDB provider
  compiling a SQL `SELECT` to an aggregation pipeline can report the pipeline it actually ran. The
  shell renders it in a collapsible **Generated query** strip on the result tab (collapsed by default,
  with copy), and hides the strip entirely when the provider reports nothing.

---

## 7. Plugin discovery today

`DatabaseProviderCatalog.Initialize` scans:

- every assembly passed by the app's composition root (`Larik/App.xaml.cs`), currently Demo and SQL
  Server;
- `<app dir>\Plugins`;
- `%LOCALAPPDATA%\Larik WinUI\Plugins`.

It instantiates every public, non-abstract type implementing `IDatabaseProvider` with a
parameterless constructor, dedupes by `Id` (first wins), and surfaces load errors in the connection
dialog.

Current limitations, by design for now:

| Limitation | Consequence |
|---|---|
| `Assembly.LoadFrom`, no `AssemblyLoadContext` | Plugin dependencies share the app's load context. Version conflicts are possible; assemblies can never be unloaded. A flat folder often works by accident because every DLL is loaded. |
| Every `*.dll` in the folder is loaded | Native or unrelated DLLs are touched; failures are reported as provider load problems. |
| No manifest or contract-version check | Compatibility relies on the add-only rule in §2. |
| `%LOCALAPPDATA%` is user-writable | A dropped DLL is trusted code running in-process. Only install plugins you trust. |
| The folder is not auto-created | Create `%LOCALAPPDATA%\Larik WinUI\Plugins` before dropping a DLL. |
| Release builds are self-contained | Trimming is off (`PublishTrimmed=false`) so XAML/x:Bind and reflection-based provider discovery survive. Revisit a trimmer root only if trimming is turned on later; validate plugins against a published build when that happens. |

**Recommendation:** first-party engines ship as built-in project references in
`Larik/App.xaml.cs` + `Larik.csproj`, using the same contracts a plugin would. That gives dependency
safety, trimming coverage and a test target. SQL Server, PostgreSQL and MongoDB already follow this rule.
Harden the loader (`AssemblyDependencyResolver`, manifest, unload) only when third-party plugins are
actually expected.

---

## 8. PostgreSQL (built in)

Shipped as `Larik.Data.Postgres` on Npgsql, registered in `Larik/App.xaml.cs`, covered by
`PostgresDialectTests` and the UI suite's provider-schema check. What it implements:

1. `PostgresProvider : IDatabaseProvider` — `Id = "postgres"`, `Dialect = "postgres"`,
   `EditorLanguage => PostgresDialect.Instance`, `DescribeEndpoint` -> `PostgreSQL · host:port`;
   settings: host, port, database, user, password, SSL mode, connect and query timeouts.
   `ProviderSettingKind` still has no `Choice`; SSL mode is a text field with a documented default.
2. `PostgresConnection : IDatabaseConnection`:
   - one physical backend **per database** (PostgreSQL cannot switch database on a live
     connection), selected by the `database` argument; `OpenAsync` opens the profile database (or
     `postgres`), and each tab still owns its own instance;
   - `pg_database` + `has_database_privilege(current_user, datname, 'CONNECT')`, so a
     least-privileged login's unreachable databases sort after the usable ones;
   - `pg_class`/`pg_attribute`/`format_type` for tables, views, materialized views and column
     types; `pg_constraint` for primary keys and foreign keys (with `ReferencedSchema`);
   - `information_schema.routines`/`parameters` for procedures and functions, with ordered
     IN/INOUT parameters (pure OUT parameters are not call arguments);
   - `pg_class.reltuples` for the reference-preview row estimate;
   - row caps and cancellation; execution splits on `;` outside strings, quoted identifiers,
     comments and dollar quotes so every statement gets its own result;
   - transactions: `SupportsTransactions => true`. The backend's own `pg_stat_activity` row is
     `active` while a probe runs, so the provider tracks the `BEGIN`/`START TRANSACTION`,
     `COMMIT`/`END`, `ROLLBACK`/`ABORT` and `ROLLBACK TO SAVEPOINT` commands it sent; a failed
     statement inside the transaction marks it aborted. Commit/rollback run on the active backend;
   - `NpgsqlConnection.Notice` fills `QueryResult.Messages` for `RAISE NOTICE`/`WARNING`.
3. `PostgresDialect : SqlLanguage, ISqlDialect` — keyword/function/table-context lists, `"` quoting,
   no `GO`, no `@` variables, dollar quotes, `E'...'` strings, nested block comments,
   `RoutineContextKeywords => ["CALL"]`, positional `NULL` arguments,
   `SELECT` for a function vs `CALL` for a procedure when the schema browser inserts one,
   `SELECT * FROM <name> LIMIT n` previews, `LIMIT` reference lookups and typed literals
   (`TRUE`/`FALSE`, bare numerics, `\x` bytea).
4. Register the project reference and `typeof(PostgresProvider).Assembly` in `App.xaml.cs`. The
   shell and editor needed no provider branch; `DescribeEndpoint` replaced the old
   `"mssql"` special case in `ConnectionViewModel`.

Still open for PostgreSQL: a `Choice` setting kind for SSL mode, overload identity in the routine
contract (two overloads with the same name collapse to the first signature), expression-position
function completion, and parenthesised/`BEGIN ATOMIC` bodies in the lexical splitter.

Two transaction limitations are inherent to text-based tracking and worth knowing: a
`COMMIT`/`ROLLBACK` executed inside a `DO` or procedure body is not visible to the tracker (it keeps
the state it saw before the call), and transaction state is per active backend, so a script that
somehow executes against a second database does not surface the first database's transaction. The
shell refuses a database switch while a transaction is open, which keeps the second case out of the
normal UI path.

---

## 9. MongoDB (built-in)

MongoDB ships as `Larik.Data.MongoDB` on MongoDB.Driver, registered in `App.xaml.cs`, covered by
`TSqlMongoCompilerTests` (translation goldens), `MongoDialectTests` (editor language) and
`MongoIntegrationTests` (a real round-trip that is a no-op unless a `mongod` answers on
`localhost:27017`, so CI stays green offline).

The editor surface is **the user's T-SQL**, which is a deliberate deviation from the older notion here
that "a non-SQL engine implements `IEditorLanguage` and must not pretend it is a SQL dialect". The
document the user writes *is* SQL, so the language implements `ISqlDialect`: highlighting, Ctrl+Enter /
F5, T-SQL completion (collections in `FROM`, sampled fields) and the schema browser's preview/lookup
statements all flow through the existing SQL seam with zero app changes. mongosh is additive: a
statement starting with `db.` is tagged `shell` via `StatementSpan.Language` and passed to the driver
as written. Keep the `IEditorLanguage`-only rule for a future genuinely non-SQL surface.

1. `MongoProvider : IDatabaseProvider` — `Id = Dialect = "mongodb"`, `DescribeEndpoint` → `MongoDB · host:port`;
   settings: host, port, database, auth source, user, password, direct connection, connect/query timeouts.
2. `MongoConnection : IDatabaseConnection` — one `MongoClient` per session (one per query tab):
   - ping + `buildInfo` on open; `ListDatabaseNames` for the database list;
   - `GetSchemaAsync` samples up to 200 documents per collection and infers fields + BSON types, with
     `_id` as the primary key. An empty or permission-denied collection yields just its name, so the
     schema is a browsing nicety, not a hard dependency;
   - `ExecuteBatchAsync` splits via `MongoTSqlDialect.Split` — the same language the editor shows, with
     mongosh statements already tagged — and stops the batch on the first error;
   - `USE [db]` changes the session's active database (reported by `GetCurrentDatabaseAsync`); the row
     cap (`EffectiveMaxRows + 1`) is enforced by appending a `$limit` unless the user already set one.
3. `MongoTSqlDialect : SqlLanguage, ISqlDialect` — reuses `GenericSqlLanguage.TokenKeywords` (the
   T-SQL token set), `[x]`/`N'...'`/`@`, `//` comments, splits on `;` with no `GO`, and overrides
   `Split` to tag mongosh spans `shell`. Completion lists carry the T-SQL surface plus Mongo helpers
   (`ObjectId`, `ISODate`, `NumberInt`, …). It also implements `GetCompletionHints` (the language-owned
   non-SQL completion seam): `db.` suggests the schema's collections and `db.collection.` suggests the
   methods the passthrough runner understands.
4. `TSqlMongoCompiler` — the pure, golden-tested translator: a lexer + recursive-descent parser +
   compiler that turns a statement into an `MSelect` (aggregation pipeline), `MInsert`/`MUpdate`/
   `MDelete` (write command) or `MUse`. SELECT covers projection, WHERE (comparison, `IN`, `BETWEEN`,
   `LIKE`, `IS NULL`, `AND`/`OR`/`NOT` — a query-language `$match` with an `$expr` fallback),
   INNER/LEFT `JOIN` (single-column `ON` → `$lookup`+`$unwind`), `GROUP BY`/aggregates/`HAVING`,
   `ORDER BY`, `TOP`/`LIMIT`/`OFFSET`, and `DISTINCT` (as a `$group`). Scalar helpers (`COALESCE`,
   `UPPER`/`LOWER`, date parts, `OBJECTID()`/`ISODATE()`…) map to aggregation operators. Every
   `MSelect` reports the pipeline it actually ran through `QueryResult.GeneratedQuery`.
5. mongosh passthrough — `MongoShellRunner` handles a small set of driver methods (`find`, `findOne`,
   `count`, `insertOne`/`insertMany`, `updateOne`/`updateMany`, `deleteOne`/`deleteMany`) when the
   argument is extended JSON; anything else is a clear "use the SQL surface" error. `ObjectId('…')`
   inside a document argument is not parsed here — the SQL surface supports it as a value.

Known, deliberate limits (mirrored in `README.md`): single-column `JOIN … ON`; an `INSERT` column list
is recommended (bare `VALUES` store under generated `c1…cN` keys); no multi-document transactions yet
(`SupportsTransactions` stays false, the shell hides the guard, and `BEGIN TRAN` reports an error); a
small mongosh subset; sampled (not enforced) schema; multi-field joins and `INSERT ... SELECT` are
rejected with a message.

---

## 10. Checklist for a new provider

- [ ] Assembly targets `net10.0`; references `Larik.Data` only at the contract boundary.
- [ ] `Id` is stable (stored in `spaces.provider_id`); `Dialect` matches snippet scopes.
- [ ] `DescribeEndpoint` reports the host/port shown in the rail (or leave the default null).
- [ ] `DescribeIdentity` names the configured login (or the integrated-auth phrase); null hides the line.
- [ ] `Settings` covers every connection field; secrets use `ProviderSettingKind.Password`.
- [ ] `CreateConnection` returns a fresh session; `OpenAsync` fails fast with a useful message.
- [ ] Per-tab state (temp tables, `SET`, transactions) lives on that instance, not in a shared field.
- [ ] If the engine supports interactive transactions, implement `SupportsTransactions`,
      `GetTransactionStateAsync`, `CommitTransactionAsync` and `RollbackTransactionAsync`; otherwise
      leave the defaults.
- [ ] `GetDatabasesAsync` works for a least-privileged login; `GetDatabaseDetailsAsync` marks names the login can see but cannot open.
- [ ] `ExecuteAsync` honours `EffectiveMaxRows`, sets `IsTruncated`/`RowLimit`, and reports errors via
      `QueryResult.Error`.
- [ ] `ExecuteBatchAsync` returns one result per result set.
- [ ] `GetSchemaAsync` returns columns, primary keys and foreign keys; populate `Schema` when the
      engine has schemas.
- [ ] Views report `Kind = View`; populate `ColumnDetails` when the engine exposes types.
- [ ] Procedures/functions (if any) come from `GetRoutinesAsync` with ordered parameters; returning
      `[]` is a valid choice that simply hides routine completion.
- [ ] `QueryResult.Statement` carries the executed SQL (needed for foreign-key reference previews),
      and `EstimateRowCountAsync` can answer cheaply when the engine has catalog metadata.
- [ ] `EditorLanguage` reuses `SqlLanguage` for SQL engines; no SQL assumptions for non-SQL engines.
- [ ] Splitting handles strings, quoted identifiers, comments and the engine's terminator.
- [ ] Tests: token/split golden cases, caret semantics, schema mapping, row cap.
- [ ] Built-in: add the project reference and `typeof(...).Assembly` in `App.xaml.cs`.
      Plugin: create `%LOCALAPPDATA%\Larik WinUI\Plugins` and drop the DLL plus dependencies.
- [ ] Run `dotnet build Larik.slnx`, `dotnet test Larik.Data.Tests\Larik.Data.Tests.csproj`, and the
      UI regression suite.

---

## 11. Known gaps (honest list)

| Area | State |
|---|---|
| MongoDB | Implemented (`Larik.Data.MongoDB`): a T-SQL surface translated to aggregation pipelines/write commands, sampled schema, mongosh passthrough. Deliberate limits: single-column `JOIN … ON`, `INSERT` column list recommended, no multi-document transactions yet (`BEGIN TRAN` errors), small mongosh method subset, sampled (not enforced) schema. See §9. |
| Completion model | SQL-clause-shaped and token-based. Keyword/function/table-context lists and identifier quoting come from the dialect; the clause/alias/column-context sets, clause continuations and the `@variable` / `INSERT (…)` / `UPDATE … SET` shapes are engine-side and T-SQL-biased. PostgreSQL reuses them; a MySQL provider should promote them into `ISqlDialect` defaults (the reference app's `src/dialects/types.ts` shows the full shape). |
| Schema-qualified completion | Implemented: references match `Schema` + `Name` and inserts are dialect-quoted (`[dbo].[users] AS [u]`, `"public"."users" AS u`). SQL Server reads `sys.schemas`; PostgreSQL reads `pg_namespace`. Views complete like tables. Overloaded PostgreSQL routines collapse to the first signature because the contract has no overload identity. |
| Statement splitting | Dollar quotes, `E'...'`, quoted identifiers and nested block comments are in `SqlLanguageOptions`; `::` casts and `$1` parameters need no splitting. Parenthesised `CREATE RULE`/`BEGIN ATOMIC` bodies and T-SQL `GO n` repeat expansion are still open. |
| Formatter | `SqlFormatter` is app-owned and keyword-based; not dialect-aware. |
| Setting kinds | Only Text/Password/Number/Boolean; no Choice or FilePath. PostgreSQL SSL mode is a documented text field until `Choice` exists. |
| Snippet dialects | `SnippetDialects.Known` is a fixed list (`mssql`, `postgres`). |
| Non-SQL completion | `IEditorLanguage.GetCompletionHints` is the seam: a language contributes completions for a document the SQL model does not understand (MongoDB uses it for `db.` and `db.collection.`). SQL providers return null and are unchanged. |
| Plugin isolation | `Assembly.LoadFrom` in the default context; no unload, no dependency resolver, no manifest. |
| Trimming | Off: `Larik.csproj` sets `PublishTrimmed=false` for Debug and Release, so WinUI/XAML and reflection-based plugin discovery survive. A `TrimmerRootAssembly` becomes relevant only if trimming is enabled. |
| Transactions | The contract exists (`SupportsTransactions`, `GetTransactionStateAsync`, commit/rollback). SQL Server implements it from `@@TRANCOUNT`/`XACT_STATE()`; PostgreSQL tracks the transaction commands it sent because the backend cannot introspect its own in-transaction state while answering. Other providers keep the defaults. |
| App exit | Closing a tab or disconnecting rolls back; closing the whole app relies on the server rolling back when its sessions end. There is no app-exit prompt. |
| A failing statement stops the rest of its `GO` batch | SQL Server executes a `GO` batch natively, so the first server error ends the remaining statements in that batch. The shell already sends each `GO` batch separately; split-and-execute per statement as well if every statement must report independently. |
| `GeneratedQuery` UI | Implemented: a collapsible **Generated query** strip on the result tab shows the native command when a provider reports one; hidden otherwise. |
