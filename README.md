# Larik — native WinUI 3 SQL studio

A native WinUI 3 / Windows App SDK shell for Larik SQL Studio, ported from the Tauri + React app,
now talking to **real databases through pluggable providers**:
**Arc-style vertical tabs, tab groups and pinned folders, workspaces that group databases, live SQL syntax highlighting, and
snippet completion that expands on Tab.**

![WinUI 3](https://img.shields.io/badge/WinUI-3-0E7A0D) ![.NET](https://img.shields.io/badge/.NET-10-512BD4) ![License](https://img.shields.io/badge/license-MIT-blue)

## Download

Published for `win-x64` and `win-arm64` from
[Releases](https://github.com/dwifuady/larik-winui/releases):

- **Installer** — `Larik-<version>-win-<arch>-setup.exe`. Per-user install to
  `%LOCALAPPDATA%\Programs\Larik`, no admin rights, a **Larik** Start menu entry,
  and it uninstalls from **Apps & Features**. It installs and runs beside the
  Tauri app, which keeps the name *Larik SQL Studio*.
- **Portable** — `Larik-<version>-win-<arch>.zip`. Unpack anywhere and run
  `Larik.exe`. Both builds keep their data in `%LOCALAPPDATA%\Larik WinUI`.

Requires Windows 10 1809+ and does not need a separate .NET install. Neither
build is code-signed, so Windows SmartScreen may warn on the first run:
**More info → Run anyway**.

The SQL Server provider needs a reachable SQL Server (LocalDB works); the
**PostgreSQL** provider needs a reachable PostgreSQL server; the **MongoDB** provider needs a
reachable `mongod`. The built-in **Demo** provider works without a server, so the app opens and
runs offline.

```
C:\dev\larik-winui\
├── Larik.slnx                    # solution (app + provider assemblies)
├── .github/workflows/            # CI build/test and tag-driven release
├── installer/Larik.iss           # Inno Setup per-user installer
├── LICENSE                       # MIT
├── THIRD-PARTY-NOTICES.md        # bundled components and licenses
├── Larik.Data/                   # provider contracts + catalog + Demo provider (net10.0)
│   ├── Contracts.cs              # IDatabaseProvider, IDatabaseConnection, QueryResult, settings schema
│   ├── EditorLanguage.cs         # IEditorLanguage/ISqlDialect, QueryToken, StatementSpan
│   ├── SqlLanguage.cs            # shared SQL lexer/splitter + generic fallback
│   ├── DatabaseProviderCatalog.cs# discovers providers from assemblies + Plugins folders
│   └── Demo/DemoProvider.cs      # in-memory provider (offline use + UI tests)
├── Larik.Data.SqlServer/         # SQL Server provider on Microsoft.Data.SqlClient
│   └── TSqlDialect.cs            # T-SQL editor language (keywords, quoting, GO, splitting)
├── Larik.Data.Postgres/          # PostgreSQL provider on Npgsql
│   ├── PostgresProvider.cs       # per-database sessions, catalog schema, transactions, notices
│   └── PostgresDialect.cs        # PostgreSQL editor language (dollar quotes, LIMIT, CALL)
├── Larik.Data.MongoDB/           # MongoDB provider on MongoDB.Driver
│   ├── MongoProvider.cs          # session, sampled schema, execution, mongosh passthrough
│   ├── MongoTSqlDialect.cs       # T-SQL editor surface + mixed-script (shell) tagging
│   └── TSqlMongoCompiler.cs      # T-SQL → aggregation pipeline / write command translator
├── Larik.Data.Tests/             # editor-language + SQL↔MongoDB translator golden tests (net10.0, xUnit)
├── docs/PROVIDERS.md             # provider handbook (contracts, blueprints, gaps)
├── AGENTS.md                     # repo rules + build/verify guide for contributors/AI agents
├── Larik/                        # WinUI 3 app (net10.0-windows10.0.26100.0, WASDK 2.6)
│   ├── Themes/StudioTheme.xaml   # DESIGN.md tokens as Light/Dark/HighContrast brushes
│   ├── Models/StudioModels.cs    # Snippet, CompletionItem
│   ├── Services/
│   │   ├── SnippetCatalog.cs     # built-in snippets ported 1:1 from src-tauri/src/storage/snippets.rs
│   │   ├── SnippetStore.cs       # SQLite snippets table (built-ins + user-defined, enabled + dialect scope)
│   │   ├── SnippetLibrary.cs     # live snippet set shared by completion and the settings page
│   │   ├── CompletionEngine.cs   # clause-aware completion, ${cursor}/${1:placeholder} expansion
│   │   ├── SqlFormatter.cs       # conservative keyword/clause formatter
│   │   ├── QuerySession.cs       # one persistent server session per query tab
│   │   ├── StudioStore.cs        # SQLite session store: spaces, tabs, archive, app_state (no passwords)
│   │   ├── SecretStore.cs        # Windows Credential Manager for provider password fields
│   │   └── StudioColors.cs       # space palette + active-tab tint
│   ├── ViewModels/               # Studio / Space / Connection / Database / Tab / TabGroup / ArchivedTab
│   ├── Editor/IEditorSurface.cs  # the editor seam: text, caret, language, completion, commands
│   ├── Controls/SqlEditorView.*  # IEditorSurface impl #1: RichEditBox + caret-anchored popup
│   ├── Controls/DragHandle.cs    # splitter with a resize cursor
│   ├── SettingsPage.xaml(.cs)    # settings shell + snippet manager (create/edit/enable/reset/delete)
│   └── MainPage.xaml(.cs)        # shell, results grid, connection dialog
└── ui-tests.ps1                  # UI Automation checks (incl. a real SQL Server round trip)
```

## Run

```powershell
cd C:\dev\larik-winui\Larik
winapp run . --detach --json     # builds, registers a debug identity, launches
```

## Requirements

- Windows 10 1809+ (`x64` or `arm64`).
- To build from source: .NET 10 SDK, [WinApp CLI](https://github.com/microsoft/winappCli)
  0.7+, and Developer Mode. Published builds are self-contained and need no
  separate .NET install.

## Verify

```powershell
dotnet build Larik.slnx
dotnet test Larik.Data.Tests\Larik.Data.Tests.csproj
cd C:\dev\larik-winui\Larik
powershell -ExecutionPolicy Bypass -File ..\ui-tests.ps1 -Target local
# While iterating, run only the ordered prefix that covers the area you touched:
powershell -ExecutionPolicy Bypass -File ..\ui-tests.ps1 -Target local -Through 'editing another workspace'
```

The UI checks are one ordered scenario (they connect workspaces and reuse tabs), so a check cannot
run alone; `-Through` runs everything up to the named check and marks the rest `SKIP`. Use it for
the cheap loop, then run the whole suite before calling a behaviour change done. Exit codes: `0`
green, `1` failure, `2` `-Through` matched nothing.

The suite drives the real window over UI Automation: the empty first run creates its workspaces
through onboarding, the workspace dots switch workspaces (click and a horizontal trackpad swipe),
new-workspace creation is gated on the create dialog and deletion archives its SQL, `sel` →
suggestion popup →
Tab accepts the expansion and lands the caret after `FROM `, Ctrl+Enter runs only the statement
under the caret and F5 runs the selection or the whole document (with `Result 2` for a batch),
syntax highlighting is applied without breaking Ctrl+Z, Ctrl+F counts matches and Enter walks them,
replace all rewrites every match as one undo step, query and result tabs drag-reorder,
a tab groups under a header, collapses and expands, a new tab on its database joins it, the
"Group by database" action creates groups and undoes them, a group pins as a folder above TABS and
returns after a workspace switch,
`FROM ` lists every table and inserts a schema-qualified, quoted name with its alias
(`[dbo].[backupset] AS [b]`), a `USE msdb` switches the tab, picker and completion schema, `Ctrl+K`
finds a table in the schema peek and inserts its quoted name, views complete like tables, `EXEC`
expands a procedure with its parameters and selects the first `NULL`, the peek also inserts a full
procedure call, the peek pins beside the editor and unpins, a `PRINT` opens a Messages tab with the
server text, `ON ` suggests a join condition, columns
are offered through their alias, `UPDATE SET` / `INSERT INTO` list the target table's columns,
declared `@variables`, `USE` databases and CTE names complete, workspace switching swaps
connection + tabs + editor, a
workspace starts disconnected until the rail's Connect action connects it, a new query asks for its
database, the picker and result grid follow the active tab (and each tab restores its own result),
a batch opens one result tab per statement, Ctrl+Enter replaces the active result tab, Ctrl+\
appends a new one, a transaction opened in one tab does not leak into another tab and shows the
`TXN` / Commit / Roll back guard, F5 splits `GO` into separate server batches, Stop cancels a
running `WAITFOR`, each result remembers its scroll row across result/query tab switches, a
1,000-row result switches tabs quickly, a wide result scrolls horizontally under its sticky header,
results are capped by the provider at 5,000 rows, cells/ranges copy as TSV and copy with headers,
Space opens the cell preview and Esc closes it, Shift+Space previews the referenced row,
the grid menu exports the whole result to a CSV file through the real save dialog,
Ctrl+Space selects a column, a header drag resizes it, the window rectangle is saved with the
session, the connection dialog renders the provider's
declared fields and its Test result, saved state is written to disk, and a real **SQL Server**
workspace is
connected, its databases listed and `sys.databases` queried (that check needs SQL Server LocalDB
or a reachable server; the workspace dialog starts with the `(localdb)\MSSQLLocalDB` profile).

The suite parks the `%LOCALAPPDATA%\Larik WinUI` SQLite files (database + WAL sidecars) before
launching, so it starts from the empty onboarding surface and creates the workspaces it reuses,
then restores the tester's files when it finishes.

## Providers: adding a database

A provider is any assembly that implements `Larik.Data.IDatabaseProvider`:

```csharp
public sealed class PostgresProvider : IDatabaseProvider
{
    public string Id => "postgres";                    // stored with the connection
    public string DisplayName => "PostgreSQL";
    public string Dialect => "postgres";               // scopes snippets/features
    public IReadOnlyList<ProviderSetting> Settings => [/* host, port, user, password… */];
    public IDatabaseConnection CreateConnection(ConnectionProfile profile) => new PgConnection(profile);
}
```

The shell and editor never reference a specific provider; only the composition root lists the
built-in assemblies. `DatabaseProviderCatalog` finds implementations in the app's referenced
provider assemblies plus every DLL dropped into:

- `<app dir>\Plugins`
- `%LOCALAPPDATA%\Larik WinUI\Plugins` (where user-installed plugins belong; the MSIX install folder is read-only)

The connection dialog builds its form from `ProviderSetting` (text/password/number/boolean), so a
new provider gets a working configuration UI with no app changes. Providers are trusted code:
only drop DLLs you have built or reviewed. Load failures are reported in the dialog instead of
crashing the app. The rail's endpoint line (host/port) is provider-driven through
`DescribeEndpoint`, so the shell does not special-case a provider id.

### Editor languages

A provider also owns how its text is painted and split. Return an `ISqlDialect` for a SQL engine or
a plain `IEditorLanguage` for a non-SQL engine from `IDatabaseProvider.EditorLanguage`; null means
the generic SQL fallback. The seam covers tokenizing on the typing debounce, statement boundaries
for Ctrl+Enter/F5, and the keyword/function/table-context lists used by completion. SQL Server's
`TSqlDialect` is the reference implementation and `PostgresDialect` is the second one. Adding
another engine (MongoDB) means a new provider assembly, not an editor change;
[`docs/PROVIDERS.md`](docs/PROVIDERS.md) has the blueprints and the contract-compatibility rules.

`TableSchema` also carries `PrimaryKey` and `ForeignKeys`. Providers that report them get
foreign-key-backed join suggestions (the SQL Server provider reads `sys.indexes` and
`sys.foreign_keys`; the Demo provider declares them). Without key metadata, completion falls back
to the desktop app's name heuristics. `TableSchema.Kind` distinguishes a view from a base table and
`ColumnDetails` adds the engine's data types; `RoutineSchema` (with ordered `RoutineParameter`s)
describes procedures and functions. All are additive, so an older provider that reports only names
and columns keeps working. The SQL Server provider reads views through `sys.objects`/`sys.columns`
and procedures with their parameters from `sys.procedures`/`sys.parameters`; completion after
`EXEC` is rendered by the dialect's `RoutineCall`, which is why PostgreSQL can use `CALL` without a
shell change.

**Foreign-key reference previews** use `QueryResult.Statement` (the SQL that produced the result,
optional and null for older providers) plus the same `TableSchema.ForeignKeys` metadata. The dialect
owns the lookup SQL through `RowsStatement` and `FormatLiteral`; `IDatabaseConnection.EstimateRowCountAsync`
(optional, `sys.partitions` on SQL Server) chooses between listing a small lookup table and fetching
only matching rows from a large one.

`ProviderSetting.IsAdvanced` marks tuning fields (port, encryption, timeouts) that belong in the
connection dialog's **Advanced** section instead of competing with the server and credentials. The
SQL Server provider's `queryTimeout` (seconds, `0` waits indefinitely) lives there: a statement
is no longer cut off at a timeout the app chose.

`IDatabaseConnection.ExecuteAsync` has an overload taking `QueryExecutionOptions`. Providers should
stop reading at `EffectiveMaxRows + 1` and set `QueryResult.IsTruncated`/`RowLimit`; the default is
5,000 rows with a 100,000 ceiling, matching the Tauri app. The legacy overload keeps older provider
assemblies working. This is an execution cap, not paging: no continuation tokens or ordering rules
are needed, so a new provider only has to stop its own reader/cursor early.

`ExecuteBatchAsync` returns one `QueryResult` per result set, which becomes one result tab. SQL
Server reads native multiple result sets with `NextResultAsync`; the Demo provider splits on `;`.
Providers that only implement the single-result method still work through a default implementation.
`QueryResult.Messages` carries informational output that is not a result set (`PRINT`, `RAISERROR`
up to severity 10, `SET STATISTICS IO/TIME`, warnings). SQL Server fills it from
`SqlConnection.InfoMessage`; the shell gives it a **Messages** tab, and providers without message
output simply leave it empty.

A provider connection is a **session**, not a shared server handle: the shell creates one per query
tab. Providers that support transactions implement `SupportsTransactions`,
`GetTransactionStateAsync`, `CommitTransactionAsync` and `RollbackTransactionAsync`; the default
interface implementations return "no transaction" and keep older providers working (the shell then
hides the transaction guard). The SQL Server provider reads `@@TRANCOUNT` and `XACT_STATE()`.

`IDatabaseConnection.GetCurrentDatabaseAsync` is the other optional member: it reports the session's
database after a batch, which is how a script's `USE` reaches the tab, rail and picker. Its default
returns null, so a provider that cannot tell simply never changes the tab's database. `TableSchema`
carries `Schema` and `ForeignKeySchema` carries `ReferencedSchema`; completion inserts a
schema-qualified name using the dialect's `QuoteIdentifier` (`[dbo].[Promotion] AS [p]`), and falls
back to the bare name for providers that report no schema.

Shipped today: `Larik.Data.SqlServer` (Microsoft.Data.SqlClient, integrated or SQL auth,
`sys.databases` for the picker, `sys.objects`/`sys.schemas`/`sys.columns` for schema-qualified
completion plus views and column types, `sys.procedures`/`sys.parameters` for routine completion,
`sys.partitions` for reference-preview row estimates, and
`DB_NAME()` so a `USE` reaches the shell), `Larik.Data.Postgres` (Npgsql, one physical backend per
database because PostgreSQL cannot switch database on a live connection, `pg_database` for the
picker, `pg_class`/`pg_attribute`/`pg_constraint` for tables, views, column types, keys and foreign
keys, `information_schema.routines`/`parameters` for `CALL`, `pg_class.reltuples` for reference
estimates, `Notice` for `RAISE NOTICE` messages, and tracked `BEGIN`/`COMMIT`/`ROLLBACK` state) and
`Larik.Data.Demo` (in-memory, used by UI tests and offline demos).

## What works in this slice

- **Vertical tabs, groups and pinned folders** per workspace, active tab tinted with the workspace
  colour (2 px strip + 26 % tint), database subtitle revealed on hover/active, close button.
  **Groups** are collapsible headers with a colour edge and a count; their tabs indent underneath,
  and a group lives in the same band as its members. **A pinned group is a folder** in the section
  above the TABS header (stacked colour mark, name, count; expands in place at the compact 36 px
  pinned height). **Group by database** is a one-shot action in the TABS header, shown only when two
  loose tabs share a database; it reports *Grouped n tabs* with an **Undo** in the status bar, and a
  new tab opened on that database then joins its group. Right-click a tab for **Move to group**,
  **New group from tab**, pin/unpin (a grouped tab pins its group) and close; right-click a header
  to rename, collapse, pin (the folder form) or ungroup. **Drag a tab to reorder it** within its
  band; dragging it across a header moves it into that group. Unpinning a group returns its tabs to
  the TABS band as loose tabs. Pinned tabs themselves are more compact (36 px); unpinning returns the
  tab to the normal rail. The order and group membership survive a restart and a workspace switch.
  **Double-click a tab title** (or right-click → Rename) to rename it
  **inline**; Enter commits, Esc cancels, clicking away commits. Closing is non-destructive: the tab
  **moves to the archive** and can be restored into its group.
  **New tabs open at the top of the unpinned section**, and the **New tab row** is a full-width
  tab-like row above them (Arc-style) rather than a small + in the header. Ctrl+T works too.
  The workspace switcher spans the full rail width; the sidebar toggle lives in the title bar
  (Ctrl+B also works). The connection state sits on one line under the workspace name with its
  single action (**Connect** / **Disconnect**) at the end of that line, so the primary action is
  beside the workspace it opens instead of in the status bar. Once connected that line also names
  the login (or **Windows authentication**); while disconnected it omits the login and keeps the
  provider instead. The database row only appears once a
  connection exists, and the toolbar badge repeats the database only while the rail is collapsed —
  it opens the same workspace panel, so connecting and switching stay reachable with the rail shut.
  The picker puts databases the login can open first, then the rest alphabetically.
- **New-tab command palette**: the New tab row opens a searchable palette instead of a modal
  database picker. Type to filter the workspace's databases, use Up/Down, and press Enter to open
  the tab (Esc or the backdrop cancels). Databases the login can open lead the list, alphabetically,
  followed by the ones it can only see; the current tab's database stays first in its group. With no
  databases available, Enter opens an unbound tab.
- **Archive & history**: the TABS header's archive button opens a page listing
  archived tabs with their workspace, database, saved SQL preview and age. Restore puts the tab
  back in its workspace (pin state, and its group when that group still exists); Delete or
  **Clear all** removes history.
  Inactivity is tracked per tab (last selected or edited). **Auto-archive** is configurable on that
  page — a toggle plus *n* days (default 30) and **Run now** — and an hourly sweep archives tabs
  untouched for *n* days. Pinned tabs, the tab currently in view, and the last tab of a workspace
  are never auto-archived. Archive state and the setting live in the same SQLite database as the
  session.
- **Workspaces, connections and databases**: a first run opens an empty **welcome surface** with one
  action — **New workspace** — instead of shipping sample data. The rail stays hidden until the first
  workspace exists, then opens automatically; deleting the last workspace hides it again and returns
  to the welcome surface. One space owns exactly one connection; that
  connection exposes several databases. Each query tab opens its own persistent **session** from
  that connection, so a transaction, temp table or `SET` option in one tab never leaks into another
  tab (the same model as SSMS, DBeaver and DataGrip). The rail's top strip carries the workspace
  switcher, whose chip shows the workspace name; the line beneath carries the connection state (dot
  **and** `Connected`/`Disconnected`) and the one action for it, so **spaces start disconnected**
  and the way to connect is beside the workspace rather than in the status bar. Running a query
  while disconnected is refused with an explicit message. The database row under it is a picker for
  the active tab, filled from the provider once connected, and it is absent while disconnected
  instead of showing a dead "not connected" control. Switching
  database updates that tab (and the space while it is active): the rail, toolbar badge, status bar,
  tab subtitle, snippet dialect and completion schema all follow. A `USE <database>` executed in a
  tab is adopted the same way: after the batch the provider reports the session's database and the
  tab, rail, picker and completion schema re-point at it, so the next run is not switched back.
  Other tabs keep their own database
  and schema. An Arc-style row of **workspace icons** at the bottom of the
  rail shows up to six visible workspaces, the current one tinted in its colour; clicking an icon
  switches workspace. Past six the row becomes a position pill (`‹ icon › 4 / 10`) that steps to the
  neighbour or opens the labelled workspace list, because ten unlabelled marks identify nothing.
  Every row in that list carries a quiet **provider chip** (`SQL Server`, `PostgreSQL`, `Demo (in-memory)`) from
  the provider's own display name, so two look-alike workspace names are distinguishable without
  opening either. The switcher no longer edits: it only switches, offers **New workspace**, and
  points to **Manage workspaces…**.
  Each workspace wears a curated Fluent icon and falls back to the first letter of its name, so no
  workspace is ever a blank; the icon is picked beside the name in the workspace dialog, and the
  colour swatch next to it opens the palette in a flyout. Choosing a colour closes the flyout and
  re-tints the icon, so the header stays one compact identity row and the connection form keeps the
  height. The active
  workspace gets a tinted chip instead of the old ring — a ring only reads as a dot — and colour is
  now a state signal (active workspace, tab tint), not the identity.
  **New workspace** opens one dialog for the icon, name, colour and connection (rare provider fields
  open from an **Advanced connection settings** row, so the form keeps its shape); nothing is created
  until **Create & connect**, so Cancel leaves the workspace list untouched. Each workspace owns a
  palette colour (icon chip, tab strip/tint and Run button).
  **Manage workspaces…** opens a dedicated **Workspaces page**: one ordered list with each
  workspace's provider, endpoint and configured identity (for example
  `PostgreSQL · db.internal:5433 · readonly_analyst`, or
  `SQL Server · (localdb)\MSSQLLocalDB · Windows authentication`). The identity comes from the
  provider's additive `DescribeIdentity` member, so the shell never assumes a `user` field. Rows drag
  to reorder the switcher and rail footer, and the order is the same `sort_order` the store already
  writes. **Hide** takes a workspace out of the rail and switcher without touching its tabs,
  sessions, connection or place in the order; **Show** returns it there. Hiding the open workspace
  takes effect in the rail after you switch away, so the current context never vanishes. **Edit**
  opens the workspace settings dialog from the list; switching stays in the rail and switcher.
  **Workspace settings**: icon, name, colour and the connection
  in one form (a workspace *is* its connection). Saving rebuilds the connection only when it actually
  changed — renaming never drops a live session — and a workspace you are not in stays disconnected
  instead of changing what is on screen.
  **Workspace settings ends with a divided danger section**: quiet red **Delete workspace**, one line
  stating the consequence, and a second confirmation. The profile and its stored secrets are removed,
  but every tab that holds SQL moves to **Archive** first, so deleting a workspace never discards the
  user's work.
  The database flyout is only about databases. Switching — from the top dropdown, the
  icons, or a two-finger horizontal trackpad swipe over the rail — slides the query content in from
  the side the new workspace lives on (left/right matches the icon order), and is skipped when
  Windows animations are turned off. A swipe moves one visible workspace per gesture and does not
  wrap at the ends.
- **Schema browser**: `Ctrl+K` (or the browse button beside the database row) opens a **peek**: one
  search field over tables, views, procedures and columns, recents first, grouped by schema, with the
  highlighted object's detail in a pane. Tables and views show their columns with types and PK/FK
  badges; procedures show their parameters and are inserted as a full `EXEC` call (never previewed).
  Enter inserts the dialect-quoted name at the caret (`[dbo].[Orders]`), Shift+Enter previews the top
  100 rows into a new result tab and reveals the pane if it was hidden, Up/Down move, Esc returns
  to the editor. The **pin** button docks
  the same surface as a 300 px inspector beside the editor (columns and parameters expand under the
  selected object); the pin unpins it. Recents are kept per workspace for the session and only track
  tables/views, and the popup never replaces the tab rail.
- **Real execution**: connect opens the provider connection and the grid renders returned
  columns/rows (NULLs dimmed, numerics right-aligned). Errors come back from the provider with
  timing. **Ctrl+Enter runs only the statement under the caret** and leaves it selected, so the
  run is visible and a second F5 repeats exactly that statement; **F5 runs the whole document**
  (or just the selection when text is selected) and splits `GO` into real server batches, because
  the driver (unlike SSMS/sqlcmd) does not understand `GO`. The toolbar Run button follows
  Ctrl+Enter, and Ctrl+\ runs the current statement into a new result tab. Run state is per tab:
  the toolbar disables Run and shows **Stop**, which cancels the command without closing the
  session. The active provider's editor language owns tokenizing and boundaries; T-SQL understands
  strings, quoted identifiers, line/block comments and `GO` separators (including indented `GO`),
  and PostgreSQL understands dollar-quoted bodies, `E'...'` escape strings, `"quoted"` identifiers
  and nested block comments.
- **PostgreSQL**: choose it in the workspace dialog like any provider. Each tab opens one physical
  backend per database (PostgreSQL cannot switch database on a live connection), so switching the
  tab's database selects the matching backend while transactions and temp tables stay on it.
  `Ctrl+K` lists tables, views, routines and columns from `pg_class`/`information_schema`, completion
  inserts `"public"."orders" AS o`, `CALL` expands a procedure with positional `NULL`s, previews and
  foreign-key lookups use `LIMIT`, `RAISE NOTICE`/`WARNING` open a Messages tab, and the `TXN` guard
  follows the `BEGIN`/`COMMIT`/`ROLLBACK` the tab issued (a failed statement inside a transaction
  marks it rollback-only).
- **MongoDB**: pick it in the workspace dialog like any provider (needs a reachable `mongod`). The
  editor surface is your T-SQL — the active language is a SQL dialect, so highlighting, Ctrl+Enter/F5,
  T-SQL completion (collections in `FROM`, sampled fields) and the schema browser all work through the
  one seam. Each statement is translated by the provider into an aggregation pipeline or a write
  command (`SELECT`/`WHERE`/`ORDER`/`GROUP`/`JOIN` → `$match`/`$sort`/`$group`/`$lookup`; `INSERT`/
  `UPDATE`/`DELETE`), and the pipeline that actually ran is shown in a collapsible *Generated query*
  strip on the result tab. `UUID('…')`/`ObjectId('…')` literals are supported, and because the sampled
  field type is known, a plain `WHERE _id = '…'` is coerced to the UUID/ObjectId binary it compares against.
  Collections are tables whose fields are sampled from a few documents (`_id` is the primary key),
  `USE [db]` switches the tab's database, and mongosh passes through — `db.users.find({...})` runs as
  written, with `db.` completing collections and `db.collection.` completing the supported methods
  (contributed by the language, not the shell). `BEGIN TRAN` reports a clear "not supported yet";
  multi-document transactions are the next iteration.
- **Server messages**: `PRINT`, `RAISERROR` up to severity 10, `SET STATISTICS IO/TIME`, optimizer
  warnings and `USE`'s *Changed database context* arrive through the provider's message channel and
  open a **Messages** tab beside the result tabs, as selectable monospace text. One Messages tab per
  batch keeps attribution honest (messages can arrive between result sets); errors keep their own
  result tab, and the status bar reports the message count.
- **Transactions, visibly**: because each tab owns a session, `BEGIN TRAN` / `COMMIT` /
  `ROLLBACK` behave per tab. After every run the app asks the provider for the session's
  transaction state (`@@TRANCOUNT`/`XACT_STATE()` on SQL Server; PostgreSQL has no nested
  transactions, so the provider tracks `BEGIN`/`COMMIT`/`ROLLBACK` and marks a failed statement's
  transaction rollback-only) and shows a `TXN` badge on the tab plus a status-bar guard
  ("1 open transaction") with **Commit** and **Roll back**. Switching tabs switches the guard;
  closing a tab rolls its transaction back;
  disconnecting, changing the connection or switching database is refused while a transaction is
  open. Like SSMS, the app stays in autocommit and does not wrap a run in a transaction
  or expose a DBeaver-style autocommit switch. Isolation remains the server default;
  `SET TRANSACTION ISOLATION LEVEL ...` works, and now only affects the tab that issued it.
- **Snippet completion**: type a trigger prefix, get a popup anchored to the caret via
  `ITextSelection.GetRect` (text-area coordinates, offset by the editor's padding), Up/Down to move,
  **Tab or Enter accepts**, Esc dismisses. In a column list, **Space ticks the highlighted column**
  (column rows carry a checkbox) so several can be picked, and one Tab/Enter inserts them
  comma-separated (`o.Id, o.Intent, o.Status`). Picks survive filtering and closing the popup while
  the caret stays at the same insertion point, so a selection can be built in several passes; moving
  to another word or statement starts fresh, and accepting clears them. Space only ticks when the
  word being typed is empty or a pick is already in progress, so a space mid-word is never swallowed.
  Expansion honours `${cursor}`, `${1:placeholder}` (left
  selected), repeat markers, and `alias.`/`table.` column completion resolved through the
  statement's aliases. Completion is clause-aware: after `FROM`/`JOIN`/`INTO`/`UPDATE`/`TABLE` it
  lists every table in the active database with its schema, and inserts a runnable name in the
  dialect's quoting — `[dbo].[Promotion] AS [p]` on SQL Server (plus a `no alias` variant); typing a
  schema (`FROM dbo.`) narrows the list and is not repeated in the insert. After
  `SELECT`/`WHERE`/`ON`/`AND`/... it lists the
  statement's columns through their aliases (`c.full_name`) — scoped to the tables of the statement,
  even when the FROM clause sits below the caret, which is how `SELECT t.` lists only `t`'s table —
  suggests join conditions after `ON` only (not after `WHERE`), offers the clause continuations
  (`GROUP BY`, `HAVING`, `ORDER BY`, `UNION`, `OPTION`) after a predicate, and the
  built-in functions (`COUNT()`, caret inside); after `UPDATE x SET` and `INSERT INTO x (` only that
  table's columns are offered (with ` = ` for SET); `DECLARE`d `@variables`, `USE` databases and
  CTEs complete too — a CTE takes its columns from an explicit list, its SELECT aliases, or the
  source table when it selects `*`, and shadows a real table of the same name. Views complete exactly
  like tables (their detail says `view`), and after `EXEC`/`EXECUTE` (`CALL` for engines that use it)
  procedures are offered with their parameters: accepting one inserts the whole call
  (`EXEC [dbo].[find_user] @id = NULL, @name = NULL`) and leaves the first `NULL` selected, so the
  signature is visible instead of memorised. Elsewhere it mixes
  snippets, keywords and schema. Ranking follows what was typed: a keyword or table that *starts*
  with the word beats a snippet that merely contains it (`wher` leads with `WHERE`, not the `selw`
  snippet), and keyword matches keep the dialect's declared order instead of sorting alphabetically.
- **Snippet manager (Settings)**: the gear in the query toolbar opens a Settings page with a
  **Snippets** section modelled on the desktop app's `SnippetsDialog`: search and category filters,
  a built-in/custom badge, and an editor for the trigger, name, category, description and expansion
  content. Built-ins can be edited, disabled and reset to their catalog default but not deleted;
  custom snippets can be created and deleted. Each snippet can be scoped to **All SQL databases**,
  **SQL Server** or **PostgreSQL**, and typing the trigger then Tab expands it in the editor. Rows
  persist in the same SQLite database as the workspace (the `snippets` table, same shape as
  `src-tauri/src/storage/snippets.rs`), with built-ins seeded by stable id so edits survive upgrades.
- **Persistence**: a SQLite database at `%LOCALAPPDATA%\Larik WinUI\larik-winui.db`
  (`larik-winui-dev.db` in Debug builds) keeps `spaces`, `space_settings`, `tabs`, `archived_tabs`,
  `snippets` and `app_state` — the same model as `src-tauri/src/storage`, extended with pin state,
  the inactivity clock and archived tabs — so spaces, connection profiles (non-secret settings),
  tabs, their SQL, pins, archive history and snippet edits survive a restart, as does the **window
  rectangle** (stored under `app_state` as DIPs, never the maximized size) and the **results-pane
  height / cell-preview width**, so the next launch opens where you left it. Workspace order and
  the **hidden-from-rail** flag also survive. WAL mode, foreign keys
  with cascade deletes, schema versioned via `PRAGMA user_version` (v2 adds the tab and archive
  columns/tables in place; v3 adds the workspace icon; v4 adds the rail visibility flag); a
  pre-SQLite `studio.json` is imported once. Spaces still start
  disconnected. Provider-declared password fields are kept in Windows Credential Manager (never in
  SQLite), so SQL-auth connections reconnect after a restart without re-entering the password.
  Corrupt state opens the welcome surface instead of failing the launch. The store is deliberately **separate from the Tauri
  app's** (`%APPDATA%\larik\larik-sql-studio\data\larik.db`): the two schemas share table names but
  not versions, so the WinUI build keeps its own file and the two apps install and run side by side.
  The first launch after this split copies the WinUI build's older `%LOCALAPPDATA%\Larik` database
  (and `studio.json`) forward once, without touching the Tauri store. Windows may redirect a
  packaged (MSIX) build's writes into
  `%LOCALAPPDATA%\Packages\<PackageFamilyName>\LocalCache\Local\Larik WinUI` — standard MSIX
  AppData redirection, not a second store; the app still addresses it as `%LOCALAPPDATA%\Larik WinUI`.
- **Result tabs and grid**: results start collapsed so the editor owns the window; the first run
  reveals the pane with a short slide from below, at the height you last dragged. **Ctrl+R** toggles
  the pane, and running a query while it is hidden opens it again. Closing the only result hides the
  pane again; a single result's header carries its own close button (multiple results close from
  their tab strip). A batch (or a
  statement returning several result sets) opens one result tab per result, DBeaver-style.
  Ctrl+Enter replaces the active result tab; Ctrl+\ appends new
  result tabs. Tabs show their row count or error, can be switched and closed, and belong to their
  query tab. **Drag a result tab to reorder it** within the query tab; the active result stays
  active and the remaining tabs renumber. Each grid is virtualized — a `ListView` realizes only
  the visible rows and recycles
  their containers, so a 1,000-row result stays quick and light to switch back to. A real grid with
  header/row/column rules and a sticky header. Click a cell or move with the arrow keys; Shift
  extends the selection; Ctrl+Space selects a column; Ctrl+A selects everything. **Space** opens a
  **cell preview** beside the grid with the full, untruncated value (the grid ellipsizes cells),
  the column type and row number, explicit NULL/empty states and a copy button. Arrow keys keep it
  in step with the focused cell; it stays open while switching result or query tabs (showing the
  active cell in the new context, and returning to the cell you left), and Esc or its close button
  turns it off until you open it again. **Shift+Space** opens the foreign-key reference instead: for a
  small lookup table the whole table is shown as a real column/row grid with the matching row
  highlighted; for a large one only the matching rows are fetched (a metadata row estimate chooses)
  and shown as record cards, where a wide row reads better in the narrow pane. A composite key
  filters on the columns present in the result. The link button toggles value/reference for the same
  cell, and the resolver only previews a reference when the source table is unambiguous. A small link
  icon in the column header marks a foreign-key column (tooltip names the target) and opens the same
  preview. JSON and XML values are
  auto-detected and pretty-printed, an **Auto / JSON / XML / Plain** selector overrides the guess
  (invalid input falls back to the raw text with a note), Copy takes the formatted value, and the
  panel's left edge can be dragged between 240 and 720 px while the grid keeps a usable width.
  Ctrl+C copies the
  selected range as TSV (spreadsheet-friendly), Ctrl+Shift+C includes column headers, and the
  context menu offers Copy / Copy with headers / Select all. **Export results…** in the same menu
  writes the whole result (not the selection) through the Windows save picker, where the file type
  *is* the format choice: CSV (RFC 4180, CRLF, header row, NULL as an empty field, UTF-8 with BOM
  so Excel reads non-ASCII) or JSON (array of row objects, real `null`s, duplicate column names
  suffixed). The status bar reports the row count and file name. Drag a header edge to resize a column;
  widths persist with that result set. Each result also remembers its vertical/horizontal scroll
  offset and active cell, so switching to another result or query tab and back returns to the same
  row. The provider caps each result at 5,000 rows (100,000 ceiling) while reading, and a truncated
  result says **Showing first 5,000 rows**. Switching query tabs restores that tab's result tabs;
  running a query while another tab is active writes to the tab that started it.
- **Find and replace**: Ctrl+F opens a compact bar over the top of the editor (Ctrl+H opens it with
  the replace row revealed); the bar counts matches (`3 of 12`, `No results`) and Enter / Shift+Enter
  or F3 / Shift+F3 walk them with wrap-around. A selection seeds the query, so select a word and
  Ctrl+F to find the next occurrence. Matches are shown as real selections, which keeps them on the
  theme's selection brush (including HighContrast) instead of fighting the syntax colours. Replace
  and **Replace all** are one undo step each, Esc (or the close button) returns to the text, and
  switching tabs closes the bar because it belongs to the editor, not the document.
- **Syntax highlighting**: the active provider's `IEditorLanguage` classifies keywords, functions,
  strings, numbers, variables and comments live with theme-aware tokens (`StudioSyntax*Brush`,
  HighContrast falls back to the system text colour). The editor tokenizes on a 140 ms debounce so
  typing never waits for a repaint. Character formatting is kept out of the undo stack: the surface
  owns text snapshots, so Ctrl+Z/Ctrl+Y still undo typing (and an accepted snippet) rather than
  colours.
- **Design system**: DESIGN.md tokens, Light/Dark/HighContrast dictionaries, mono editor,
  status bar with the transient message + `Ln/Col` (connection state lives in the rail),
  splitter between editor and results. **Settings** (appearance, snippets, about) and the
  **Workspaces** manager are app destinations: Workspaces opens from the switcher, while Settings
  sits on its gear in the rail footer beside the workspace dots - the query toolbar only carries
  editor actions (Format, Run, Stop). Appearance offers **Use Windows setting / Light / Dark**;
  the choice is saved in `app_state` (`theme`), applied before the first frame, and runtime
  switches move the content, result rows, editor syntax colours, caption buttons and Mica together
  without a restart.
- **Editor seam**: the shell never touches a `RichEditBox`. It drives `IEditorSurface`
  (text, caret, `EditorLanguage`, `CompletionSource`, `TextChanged`/`CaretChanged`/`CommandRequested`,
  `FocusEditor`), so the editor can be swapped for a WebView2/Monaco island without changing the
  rail, tabs, spaces, toolbar or results. `Services/StudioCompletionSource.cs` bridges snippets +
  schema + the active dialect into whichever surface is mounted.
- **Native chrome**: the window uses a `MicaBackdrop`; the rail, toolbar and status bar are
  transparent so they read as one chrome surface with the title bar, and only real content sits on
  Fluent card brushes (`CardBackgroundFillColorDefaultBrush`). Falls back to the design's solid
  background when `MicaController.IsSupported()` is false. A forced Light/Dark choice recreates the
  backdrop so Mica matches the content instead of the OS, and `AppWindowTitleBar.PreferredTheme`
  keeps the caption buttons in the same mode.

## Deliberate gaps (next iterations)

| Gap | Why / next step |
|---|---|
| Four providers ship | SQL Server, PostgreSQL, MongoDB and the in-memory Demo. Each is an assembly behind the same contracts. |
| MongoDB is a T-SQL surface over a NoSQL engine | The provider translates your T-SQL into aggregation pipelines / write commands and passes mongosh (`db.c.method(…)`) through. Deliberately limited: single-column `JOIN … ON` (a multi-key ON is rejected), an `INSERT` column list is recommended (bare `VALUES` store under generated keys), a handful of mongosh methods, and no multi-document transactions yet (`BEGIN TRAN` errors). The result-tab *Generated query* strip shows the pipeline that actually ran. |
| Routines cover procedures, not functions | `RoutineSchema` already models `Function`, but SQL Server reads `sys.procedures` only and completion offers procedures only after `EXEC`/`CALL`. PostgreSQL reads functions too, but they are not offered in expression positions yet. Adding `FN`/`IF`/`TF` objects and expression-position function completion is the next step. |
| PostgreSQL overloads collapse to one routine | The contract identifies a routine by schema + name, so the provider keeps the first overload's signature instead of showing two `CALL` candidates with the same name. A contract-level routine identity is the fix. |
| Pinned inspector is session-only | The pin and its 300 px width reset on restart; persist them in `app_state` with the window rectangle if it becomes part of the daily layout. |
| Completion context is token-based | Subquery aliases and `UPDATE ... FROM alias` targets still need full statement parsing (the Tauri app uses an AST). Join fallbacks for providers without foreign keys stay name heuristics. Keyword/function/table-context lists come from the active dialect (`TSqlDialect`/`PostgresDialect` today). |
| Reference previews need an unambiguous column map | Shift+Space maps a result column to its source table with the same lexical FROM/JOIN resolver as completion. Aliased output columns, duplicate names across joined tables and multi-statement batches fall back to the value preview instead of risking the wrong foreign key; a SQL AST would close this. |
| Current-statement splitting is lexical | Each provider owns its splitter (`IEditorLanguage`). T-SQL handles strings, identifiers, comments, indented `GO` statements and `GO` batch splitting for F5; PostgreSQL adds dollar-quoted bodies, `E'...'` strings, quoted identifiers and nested comments. Semicolons inside parenthesised `CREATE RULE`/`BEGIN ATOMIC` bodies and nested `BEGIN/END` awareness still need the same AST work as completion. |
| Result rows are capped, not paged | The provider stops at 5,000 rows (100,000 ceiling) to bound memory. Paging/continuation would need a stable ordering contract per driver; start there if deep result browsing becomes a requirement. |
| A failing statement stops the rest of its GO batch | SQL Server executes a GO batch natively, so the first server error ends the remaining statements in that batch. F5 already sends each `GO` batch separately; split-and-execute per statement as well (as the Tauri drivers do) if every statement must report independently. |
| `GO n` repeat counts are parsed but not expanded | The splitter accepts `GO 10` and sends the batch once. Expand it in `SplitBatches` if scripts rely on SSMS's repeat behaviour. |
| No autocommit / manual-commit switch, no isolation dropdown | Deliberately mirrors SSMS for SQL Server: each statement autocommits, `BEGIN TRAN`/`COMMIT` are explicit, and isolation is the server default (`SET TRANSACTION ISOLATION LEVEL` remains available per tab). A DBeaver/DataGrip-style manual-commit mode is a product decision, not a SQL-standard requirement. |
| App exit has no transaction prompt | Closing one tab or disconnecting is guarded and rolls back; closing the whole app relies on SQL Server rolling back when the process's sessions end. |
| Tab organisation is one level | Groups and pinned folders ship; nested groups, drag-one-pinned-tab-onto-another stacking, dragging a group header to reorder groups, a manual group colour picker and groups that span workspaces are not. Command palette and MCP remain future work. |
| No snippet import | The manager handles built-ins and custom snippets; DBeaver template XML import from the web app is not ported yet. |

## WinUI notes worth remembering

- WinUI has no `DataGrid` and no Monaco; the editor surface is the real porting cost.
- `ListView` with `SelectionMode="None"` gives no `Invoke` pattern, so tabs are activated with
  `ItemClick` — keyboard activation via Enter still needs an explicit handler.
- The tab rail is **one flat `ListView` per band** (pinned, tabs) whose items are either a
  `TabGroupViewModel` header or a `TabViewModel`, chosen by `TabRailTemplateSelector`. Nesting a
  `ListView` per group would lose native drag-reorder and give every group its own scroll surface.
  `DragItemsStarting` cancels header drags; `SyncRailOrder` rebuilds membership from where a tab
  lands relative to the nearest header.
- `KeyboardAccelerator` on the page does **not** win over `RichEditBox` shortcuts (Ctrl+B is bold),
  so shell shortcuts are handled in `Editor_PreviewKeyDown` and
  `DisabledFormattingAccelerators="All"` keeps rich formatting off.
- `RichEditBox` paints its own fill from the `TextControlBackground*` theme resources, including a
  different one while focused, which hides any parent card. Override those plus the
  `TextControlBorderBrush*` keys to `Transparent` so the card surface shows through.
- Programmatic `ITextCharacterFormat` changes are undoable, so a syntax highlighter that recolours
  after every keystroke would make Ctrl+Z revert colours. `SqlEditorView` disables the control's
  undo stack and keeps text snapshots itself (coalescing typing, restoring the caret); Ctrl+Z/Y are
  handled in `PreviewKeyDown`. Formatting also raises delayed `TextChanged` events, so that handler
  ignores events whose text is unchanged.
- `ListView.CanReorderItems` reorders the bound `ObservableCollection` but not the model behind a
  mirror. Both tab strips write the new order back on `DragItemsCompleted` (query tabs to the space,
  result tabs to the query tab) and reindex the result titles.
- `Application.RequestedTheme` can only be assigned during startup, so the saved appearance is read
  before the first window and a runtime switch moves the root `FrameworkElement.RequestedTheme`
  instead. `Application.Current.Resources[key]` still resolves against the startup app theme, so
  code that caches brushes (result rows, headers) uses `ThemeService.GetBrush` with the control's
  `ActualTheme`; XAML `ThemeResource` values update on their own.
- `AppWindow.Resize` takes physical pixels; size in DIPs and scale by `GetDpiForWindow`.
- MVVM Toolkit `[ObservableProperty]` on fields warns (MVVMTK0045) under WinRT/AOT; partial
  properties keep the build warning-free and AOT-ready.
- `winapp ui` synthetic input only reaches the app while it owns the foreground, and Windows blocks
  a background process from stealing focus. The test harness presses a synthetic Alt before
  `SetForegroundWindow` to unlock it; without that, clicks and keys land in whatever window has focus
  (the IDE), while UIA `invoke`/`get-value` still work — a confusing half-passing suite.

## Releasing

The git tag is the version. Tag a commit and push it; CI builds both
architectures as portable ZIPs and Inno Setup installers, then publishes them
with SHA-256 checksums.

```powershell
git tag v0.1.0
git push origin v0.1.0
```

Keep `Version` in `Larik/Larik.csproj` in sync for local builds; the workflow
overrides it from the tag. The release publishes these asset names:
`Larik-<version>-win-<arch>-setup.exe`, `Larik-<version>-win-<arch>.zip`, and
`SHA256SUMS.txt`.

## License

Larik is free software under the **MIT License** (see `LICENSE`).
Bundled components and their licenses are listed in
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
