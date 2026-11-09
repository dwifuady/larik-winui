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
| `IDatabaseProvider` | Identity (`Id`, `DisplayName`, `Dialect`), settings, `CreateConnection`, `EditorLanguage`. |
| `IDatabaseConnection` | One session: `OpenAsync`/`CloseAsync`, `State`, `ServerVersion`, `GetDatabasesAsync`, `ExecuteAsync`, `ExecuteBatchAsync`, `GetSchemaAsync`, `GetRoutinesAsync` (optional; procedures), `EstimateRowCountAsync` (optional; reference-preview size hint), `GetCurrentDatabaseAsync` (optional; reports a `USE` to the shell), plus optional transaction state/commit/rollback. |
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
   `GetRoutinesAsync` returns `[]`, `EstimateRowCountAsync` returns null, and the dialect's
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
- **Non-SQL engine** (MongoDB) → `IEditorLanguage` only. Implement your own tokenizer/splitter; do
  **not** pretend it is a SQL dialect.

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

Not yet configurable, and deliberately deferred: dollar-quoted strings (`$$...$$`), `E'...'`
strings, `::` casts and `$1` parameters. When PostgreSQL needs them, extend `SqlLanguageOptions`
additively rather than forking the scanner.

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
  remain SQL text; the shell only offers a whole-transaction commit/rollback.
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
(`[dbo].[p] @a = NULL, @b = NULL`), and PostgreSQL should override it with positional `NULL`s.
`RoutineInvocation` prefixes the first keyword for the schema browser's Enter action.
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
  app currently stores it but does not display it; a result-tab "generated query" bar is future work.

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
| Release builds are trimmed | `Larik.Data` has no explicit trimmer root yet. Reflection-only members could be trimmed in a published build; validate plugins against a published build, not just Debug. |

**Recommendation:** first-party engines (PostgreSQL next) ship as built-in project references in
`Larik/App.xaml.cs` + `Larik.csproj`, using the same contracts a plugin would. That gives dependency
safety, trimming coverage and a test target. Harden the loader (`AssemblyDependencyResolver`,
manifest, unload) only when third-party plugins are actually expected.

---

## 8. Blueprint: PostgreSQL (built-in next)

1. New project `Larik.Data.Postgres` (`net10.0`), reference `Larik.Data` and `Npgsql`.
2. `PostgresProvider : IDatabaseProvider` — `Id = "postgres"`, `Dialect = "postgres"`,
   `EditorLanguage => PostgresDialect.Instance`, settings: host, port, database, user, password,
   SSL mode. `ProviderSettingKind` has no `Choice` yet; use a text field with a documented default
   for now. Adding `Choice` is a small contract + dialog change (and an enum addition), not something
   a plugin can do on its own.
3. `PostgresConnection : IDatabaseConnection`:
   - one physical connection **per database** (Postgres cannot switch database on a live
     connection), or a pool keyed by database; `ExecuteAsync(sql, database, ...)` selects it;
   - `GetDatabasesAsync` from `pg_database`;
   - `GetSchemaAsync` from `information_schema`/`pg_catalog`, populating `TableSchema.Schema`,
     `TableSchema.Kind` (base table vs view), `ColumnDetails` from `format_type(a.atttypid,
     a.atttypmod)`, and `ForeignKeySchema.ReferencedSchema`;
   - `GetRoutinesAsync` from `information_schema.routines` (`routine_type` maps to
     `Procedure`/`Function`) and `information_schema.parameters` (or `pg_proc.proargtypes` for
     unnamed arguments), ordered by `ordinal_position`;
   - row caps and cancellation;
   - `ExecuteBatchAsync` may split on `;` only outside strings/comments/dollar quotes;
   - transactions: `SupportsTransactions => true`, `GetTransactionStateAsync` mapping
     `TransactionStatus`/`idle in transaction`, and commit/rollback executed on the current
     per-database connection. Remember the shell refreshes state after every batch, including a
     failed one.
4. `PostgresDialect : SqlLanguage, ISqlDialect` — keywords, functions, `"` quoting, no `GO`, no
   `@` variables. `RoutineContextKeywords` should be `CALL` (procedures), and `RoutineCall` should
   render positional arguments (`"public"."refresh_stats"(NULL, NULL)`, or `CALL ...` for a
   procedure); scalar functions used in expressions are a later completion scope. Override
   `PreviewStatement` with `SELECT * FROM <name> LIMIT n`, `RowsStatement` with `LIMIT` and a typed
   `FormatLiteral`, and answer `EstimateRowCountAsync` from `pg_class.reltuples` when available. Add
   dollar-quote support to `SqlLanguageOptions` when you get there.
5. Register in `Larik/App.xaml.cs` (`typeof(PostgresProvider).Assembly`) and reference the project
   from `Larik.csproj`. No shell/editor changes.
6. Add contract tests: split/token cases, schema mapping, execution/row-cap against a real server.

Still open for PostgreSQL: a `Choice`/`FilePath` setting kind, and provider-driven
`ConnectionViewModel.Endpoint` (today it special-cases `"mssql"`).

---

## 9. Blueprint: MongoDB (plugin)

MongoDB is **not** a SQL dialect. The reference Tauri app (`C:\dev\larik-sql-studio`) demonstrates
the shape:

- a dedicated editor language (`src/utils/mongo/language.ts`) that accepts **mongosh and SQL
  SELECT**;
- a splitter that tags each statement (`shell` / `line` / `sql`) and splits on `;`, with line
  statements ending at newline (`src/utils/mongo/shell.ts`);
- SQL SELECT compiled to an aggregation pipeline entirely inside the driver
  (`src-tauri/src/db/mongodb/sql.rs`), reported through `generated_query`;
- Mongo-specific completion (`src/utils/mongo/completion.ts`), not the SQL completion engine.

In this app:

```csharp
public sealed class MongoProvider : IDatabaseProvider
{
    public string Id => "mongodb";
    public string Dialect => "mongodb";
    public IEditorLanguage? EditorLanguage => MongoLanguage.Instance; // NOT ISqlDialect
}

public sealed class MongoLanguage : IEditorLanguage
{
    public string Id => "mongodb";
    public IReadOnlyList<QueryToken> Tokenize(string? text) { /* JS-flavoured */ }
    public IReadOnlyList<StatementSpan> Split(string? text) { /* tags: "shell"/"line"/"sql" */ }
}
```

What works with zero app edits: highlighting, Ctrl+Enter/F5 boundaries (including mixed scripts),
snippet scoping, and `QueryResult.GeneratedQuery`. What still needs one *future additive* seam:
non-SQL completion (collection names, shell helpers). Do not build it until MongoDB is real; when
you do, add a per-language completion contributor behind `CompletionQuery.Language`.

Keep the SQL-on-Mongo translator inside the provider. It is provider code, not app code.

---

## 10. Checklist for a new provider

- [ ] Assembly targets `net10.0`; references `Larik.Data` only at the contract boundary.
- [ ] `Id` is stable (stored in `spaces.provider_id`); `Dialect` matches snippet scopes.
- [ ] `Settings` covers every connection field; secrets use `ProviderSettingKind.Password`.
- [ ] `CreateConnection` returns a fresh session; `OpenAsync` fails fast with a useful message.
- [ ] Per-tab state (temp tables, `SET`, transactions) lives on that instance, not in a shared field.
- [ ] If the engine supports interactive transactions, implement `SupportsTransactions`,
      `GetTransactionStateAsync`, `CommitTransactionAsync` and `RollbackTransactionAsync`; otherwise
      leave the defaults.
- [ ] `GetDatabasesAsync` works for a least-privileged login.
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
| PostgreSQL / MongoDB | Not implemented. This seam exists so they are additive. |
| Completion model | SQL-clause-shaped and token-based. Keyword/function/table-context lists and identifier quoting come from the dialect; the clause/alias/column-context sets, clause continuations and the `@variable` / `INSERT (…)` / `UPDATE … SET` shapes are engine-side and T-SQL-biased. A PostgreSQL/MySQL provider should promote those into `ISqlDialect` defaults (the reference app's `src/dialects/types.ts` shows the full shape). |
| Schema-qualified completion | Implemented: references match `Schema` + `Name` and inserts are dialect-quoted (`[dbo].[users] AS [u]`, `public.users AS u`); SQL Server reads `sys.schemas`. Views/routines are not in the schema model, so only base tables complete. |
| Statement splitting | Dollar quotes, `E'...'`, `::` and `$1` are not in `SqlLanguageOptions` yet. T-SQL `GO n` repeat counts are parsed but not expanded. |
| Formatter | `SqlFormatter` is app-owned and keyword-based; not dialect-aware. |
| Setting kinds | Only Text/Password/Number/Boolean; no Choice or FilePath. |
| Connection endpoint label | `ConnectionViewModel.Endpoint` still special-cases `"mssql"`. |
| Snippet dialects | `SnippetDialects.Known` is a fixed list (`mssql`, `postgres`). |
| Non-SQL completion | No contributor seam yet (see MongoDB blueprint). |
| Plugin isolation | `Assembly.LoadFrom` in the default context; no unload, no dependency resolver, no manifest. |
| Trimming | Release uses `PublishTrimmed`; no `TrimmerRootAssembly` for `Larik.Data` yet. |
| Transactions | The contract exists (`SupportsTransactions`, `GetTransactionStateAsync`, commit/rollback). SQL Server implements it; other providers keep the defaults until they map their own state. |
| App exit | Closing a tab or disconnecting rolls back; closing the whole app relies on the server rolling back when its sessions end. There is no app-exit prompt. |
| A failing statement stops the rest of its `GO` batch | SQL Server executes a `GO` batch natively, so the first server error ends the remaining statements in that batch. The shell already sends each `GO` batch separately; split-and-execute per statement as well if every statement must report independently. |
| `GeneratedQuery` UI | Field exists; no result-tab bar yet. |
