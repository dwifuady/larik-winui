# Larik — WinUI 3 port

A native WinUI 3 / Windows App SDK shell for Larik SQL Studio, ported from the Tauri + React app,
now talking to **real databases through pluggable providers**:
**Arc-style vertical tabs, workspaces that group databases, live SQL syntax highlighting, and
snippet completion that expands on Tab.**

```
C:\dev\larik-winui\
├── Larik.slnx                    # solution (app + provider assemblies)
├── Larik.Data/                   # provider contracts + catalog + Demo provider (net10.0)
│   ├── Contracts.cs              # IDatabaseProvider, IDatabaseConnection, QueryResult, settings schema
│   ├── EditorLanguage.cs         # IEditorLanguage/ISqlDialect, QueryToken, StatementSpan
│   ├── SqlLanguage.cs            # shared SQL lexer/splitter + generic fallback
│   ├── DatabaseProviderCatalog.cs# discovers providers from assemblies + Plugins folders
│   └── Demo/DemoProvider.cs      # in-memory provider (sample workspaces + UI tests)
├── Larik.Data.SqlServer/         # SQL Server provider on Microsoft.Data.SqlClient
│   └── TSqlDialect.cs            # T-SQL editor language (keywords, quoting, GO, splitting)
├── Larik.Data.Tests/             # editor-language golden tests (net10.0, xUnit)
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
│   ├── ViewModels/               # Studio / Space / Connection / Database / Tab / ArchivedTab
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

The suite drives the real window over UI Automation: tab switching, the workspace dots switch
workspaces (click and a horizontal trackpad swipe), new-workspace creation is gated on the create
dialog, `sel` → suggestion popup →
Tab accepts the expansion and lands the caret after `FROM `, Ctrl+Enter runs only the statement
under the caret and F5 runs the selection or the whole document (with `Result 2` for a batch),
syntax highlighting is applied without breaking Ctrl+Z, query and result tabs drag-reorder,
`FROM ` lists every table and appends a generated alias, `ON ` suggests a join condition, columns
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
Ctrl+Space selects a column, a header drag resizes it, the connection dialog renders the provider's
declared fields and its Test result, saved state is written to disk, and a real **SQL Server**
workspace is
connected, its databases listed and `sys.databases` queried (that check needs SQL Server LocalDB
or a reachable server; it is the seeded `(localdb)\MSSQLLocalDB` profile).

The suite parks the `%LOCALAPPDATA%\Larik WinUI` SQLite files (database + WAL sidecars) before
launching, so it always starts from the seeded workspaces, and restores the tester's files when it
finishes.

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
crashing the app.

### Editor languages

A provider also owns how its text is painted and split. Return an `ISqlDialect` for a SQL engine or
a plain `IEditorLanguage` for a non-SQL engine from `IDatabaseProvider.EditorLanguage`; null means
the generic SQL fallback. The seam covers tokenizing on the typing debounce, statement boundaries
for Ctrl+Enter/F5, and the keyword/function/table-context lists used by completion. SQL Server's
`TSqlDialect` is the reference implementation. Adding PostgreSQL or MongoDB means a new provider
assembly, not an editor change; [`docs/PROVIDERS.md`](docs/PROVIDERS.md) has the blueprints and the
contract-compatibility rules.

`TableSchema` also carries `PrimaryKey` and `ForeignKeys`. Providers that report them get
foreign-key-backed join suggestions (the SQL Server provider reads `sys.indexes` and
`sys.foreign_keys`; the Demo provider declares them). Without key metadata, completion falls back
to the desktop app's name heuristics.

`ProviderSetting.IsAdvanced` marks tuning fields (port, encryption, timeouts) that belong in the
connection dialog's **Advanced** section instead of competing with the server and credentials.

`IDatabaseConnection.ExecuteAsync` has an overload taking `QueryExecutionOptions`. Providers should
stop reading at `EffectiveMaxRows + 1` and set `QueryResult.IsTruncated`/`RowLimit`; the default is
5,000 rows with a 100,000 ceiling, matching the Tauri app. The legacy overload keeps older provider
assemblies working. This is an execution cap, not paging: no continuation tokens or ordering rules
are needed, so a new provider only has to stop its own reader/cursor early.

`ExecuteBatchAsync` returns one `QueryResult` per result set, which becomes one result tab. SQL
Server reads native multiple result sets with `NextResultAsync`; the Demo provider splits on `;`.
Providers that only implement the single-result method still work through a default implementation.

A provider connection is a **session**, not a shared server handle: the shell creates one per query
tab. Providers that support transactions implement `SupportsTransactions`,
`GetTransactionStateAsync`, `CommitTransactionAsync` and `RollbackTransactionAsync`; the default
interface implementations return "no transaction" and keep older providers working (the shell then
hides the transaction guard). The SQL Server provider reads `@@TRANCOUNT` and `XACT_STATE()`.

Shipped today: `Larik.Data.SqlServer` (Microsoft.Data.SqlClient, integrated or SQL auth,
`sys.databases` for the picker, `sys.tables`/`sys.columns` for completion) and
`Larik.Data.Demo` (in-memory, used by the sample workspaces).

## What works in this slice

- **Vertical tabs** per workspace, active tab tinted with the workspace colour (2 px strip + 26 % tint),
  database subtitle revealed on hover/active, close button. **Pinned tabs live in their own section
  above the TABS header** and are more compact (36 px); unpinning returns the tab to the normal rail.
  **Drag a tab to reorder it** within its section; the new order is written back to the workspace
  (and survives a restart). **Double-click a tab title** (or right-click → Rename) to rename it
  **inline**; Enter commits, Esc cancels, clicking away commits. Right-click also pins/unpins and
  closes. Closing is non-destructive: the tab **moves to the archive** and can be restored.
  **New tabs open at the top of the unpinned section**, and the **New tab row** is a full-width
  tab-like row above them (Arc-style) rather than a small + in the header. Ctrl+T works too.
  The workspace switcher spans the full rail width; the sidebar toggle lives in the title bar
  (Ctrl+B also works). The connection state sits on one line under the workspace name with its
  single action (**Connect** / **Disconnect**) at the end of that line, so the primary action is
  beside the workspace it opens instead of in the status bar. The database row only appears once a
  connection exists, and the toolbar badge repeats the database only while the rail is collapsed —
  it opens the same workspace panel, so connecting and switching stay reachable with the rail shut.
- **New-tab command palette**: the New tab row opens a searchable palette instead of a modal
  database picker. Type to filter the workspace's databases, use Up/Down, and press Enter to open
  the tab (Esc or the backdrop cancels). With no databases available, Enter opens an unbound tab.
- **Archive & history**: the TABS header's archive button opens a page listing
  archived tabs with their workspace, database, saved SQL preview and age. Restore puts the tab
  back in its workspace (pin state and section included); Delete or **Clear all** removes history.
  Inactivity is tracked per tab (last selected or edited). **Auto-archive** is configurable on that
  page — a toggle plus *n* days (default 30) and **Run now** — and an hourly sweep archives tabs
  untouched for *n* days. Pinned tabs, the tab currently in view, and the last tab of a workspace
  are never auto-archived. Archive state and the setting live in the same SQLite database as the
  session.
- **Workspaces, connections and databases**: one space owns exactly one connection; that
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
  tab subtitle, snippet dialect and completion schema all follow. Other tabs keep their own database
  and schema. An Arc-style row of workspace dots at the bottom of the
  rail shows every workspace with the current one ringed; clicking a dot switches workspace.
  **New workspace** opens one dialog for the name, colour and connection (rare provider fields live
  under **Advanced**); nothing is created until **Create & connect**, so Cancel leaves the workspace
  list untouched. Each workspace owns a palette colour (dot, tab strip/tint and Run button).
  **The pencil on a workspace row opens Workspace settings**: name, colour and the connection in one
  form (a workspace *is* its connection), editable for any workspace without leaving the current
  one. Saving rebuilds the connection only when it actually changed — renaming never drops a live
  session — and a workspace you are not in stays disconnected instead of changing what is on screen.
  The database flyout is only about databases. Switching — from the top dropdown, the
  dots, or a two-finger horizontal trackpad swipe over the rail — slides the query content in from
  the side the new workspace lives on (left/right matches the dot order), and is skipped when
  Windows animations are turned off. A swipe moves one workspace per gesture and does not wrap at
  the ends.
- **Real execution**: connect opens the provider connection and the grid renders returned
  columns/rows (NULLs dimmed, numerics right-aligned). Errors come back from the provider with
  timing. **Ctrl+Enter runs only the statement under the caret** and leaves it selected, so the
  run is visible and a second F5 repeats exactly that statement; **F5 runs the whole document**
  (or just the selection when text is selected) and splits `GO` into real server batches, because
  the driver (unlike SSMS/sqlcmd) does not understand `GO`. The toolbar Run button follows
  Ctrl+Enter, and Ctrl+\ runs the current statement into a new result tab. Run state is per tab:
  the toolbar disables Run and shows **Stop**, which cancels the command without closing the
  session. The active provider's editor language owns tokenizing and boundaries; T-SQL understands
  strings, quoted identifiers, line/block comments and `GO` separators (including indented `GO`).
- **Transactions, visibly**: because each tab owns a session, `BEGIN TRAN` / `COMMIT` /
  `ROLLBACK` behave per tab. After every run the app reads `@@TRANCOUNT` and `XACT_STATE()` and shows
  a `TXN` badge on the tab plus a status-bar guard ("1 open transaction") with **Commit** and
  **Roll back**. Switching tabs switches the guard; closing a tab rolls its transaction back;
  disconnecting, changing the connection or switching database is refused while a transaction is
  open. Like SSMS, the app stays in SQL Server autocommit and does not wrap a run in a transaction
  or expose a DBeaver-style autocommit switch. Isolation remains the server default;
  `SET TRANSACTION ISOLATION LEVEL ...` works, and now only affects the tab that issued it.
- **Snippet completion**: type a trigger prefix, get a popup anchored to the caret via
  `ITextSelection.GetRect` (text-area coordinates, offset by the editor's padding), Up/Down to move,
  **Tab or Enter accepts**, Esc dismisses. Expansion honours `${cursor}`, `${1:placeholder}` (left
  selected), repeat markers, and `alias.`/`table.` column completion resolved through the
  statement's aliases. Completion is clause-aware: after `FROM`/`JOIN`/`INTO`/`UPDATE`/`TABLE` it
  lists every table in the active database, appending a generated alias in join positions
  (`customers AS c`, plus a `no alias` variant); after `SELECT`/`WHERE`/`ON`/`AND`/... it lists the
  statement's columns through their aliases (`c.full_name`), offers join conditions (declared
  foreign keys first, then `xxx_id`/shared-column heuristics — `o.customer_id = c.id`), and the
  built-in functions (`COUNT()`, caret inside); after `UPDATE x SET` and `INSERT INTO x (` only that
  table's columns are offered (with ` = ` for SET); `DECLARE`d `@variables`, `USE` databases and
  CTEs complete too — a CTE takes its columns from an explicit list, its SELECT aliases, or the
  source table when it selects `*`, and shadows a real table of the same name. Elsewhere it mixes
  snippets, keywords and schema.
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
  tabs, their SQL, pins, archive history and snippet edits survive a restart. WAL mode, foreign keys
  with cascade deletes, schema versioned via `PRAGMA user_version` (v2 adds the tab and archive
  columns/tables in place); a pre-SQLite `studio.json` is imported once. Spaces still start
  disconnected. Provider-declared password fields are kept in Windows Credential Manager (never in
  SQLite), so SQL-auth connections reconnect after a restart without re-entering the password.
  Corrupt state falls back to the seeded spaces. The store is deliberately **separate from the Tauri
  app's** (`%APPDATA%\larik\larik-sql-studio\data\larik.db`): the two schemas share table names but
  not versions, so the WinUI build keeps its own file and the two apps install and run side by side.
  The first launch after this split copies the WinUI build's older `%LOCALAPPDATA%\Larik` database
  (and `studio.json`) forward once, without touching the Tauri store. Windows may redirect a
  packaged (MSIX) build's writes into
  `%LOCALAPPDATA%\Packages\<PackageFamilyName>\LocalCache\Local\Larik WinUI` — standard MSIX
  AppData redirection, not a second store; the app still addresses it as `%LOCALAPPDATA%\Larik WinUI`.
- **Result tabs and grid**: a batch (or a statement returning several result sets) opens one result
  tab per result, DBeaver-style. Ctrl+Enter replaces the active result tab; Ctrl+\ appends new
  result tabs. Tabs show their row count or error, can be switched and closed, and belong to their
  query tab. **Drag a result tab to reorder it** within the query tab; the active result stays
  active and the remaining tabs renumber. Each grid is virtualized — a `ListView` realizes only
  the visible rows and recycles
  their containers, so a 1,000-row result stays quick and light to switch back to. A real grid with
  header/row/column rules and a sticky header. Click a cell or move with the arrow keys; Shift
  extends the selection; Ctrl+Space selects a column; Ctrl+A selects everything. Ctrl+C copies the
  selected range as TSV (spreadsheet-friendly), Ctrl+Shift+C includes column headers, and the
  context menu offers Copy / Copy with headers / Select all. Drag a header edge to resize a column;
  widths persist with that result set. Each result also remembers its vertical/horizontal scroll
  offset and active cell, so switching to another result or query tab and back returns to the same
  row. The provider caps each result at 5,000 rows (100,000 ceiling) while reading, and a truncated
  result says **Showing first 5,000 rows**. Switching query tabs restores that tab's result tabs;
  running a query while another tab is active writes to the tab that started it.
- **Syntax highlighting**: the active provider's `IEditorLanguage` classifies keywords, functions,
  strings, numbers, variables and comments live with theme-aware tokens (`StudioSyntax*Brush`,
  HighContrast falls back to the system text colour). The editor tokenizes on a 140 ms debounce so
  typing never waits for a repaint. Character formatting is kept out of the undo stack: the surface
  owns text snapshots, so Ctrl+Z/Ctrl+Y still undo typing (and an accepted snippet) rather than
  colours.
- **Design system**: DESIGN.md tokens, Light/Dark/HighContrast dictionaries, mono editor,
  status bar with the transient message + `Ln/Col` (connection state lives in the rail),
  splitter between editor and results. **Settings** (snippets, about) is an app destination, so its
  gear sits in the rail footer beside the workspace dots — the query toolbar only carries editor
  actions (Format, Run, Stop).
- **Editor seam**: the shell never touches a `RichEditBox`. It drives `IEditorSurface`
  (text, caret, `EditorLanguage`, `CompletionSource`, `TextChanged`/`CaretChanged`/`CommandRequested`,
  `FocusEditor`), so the editor can be swapped for a WebView2/Monaco island without changing the
  rail, tabs, spaces, toolbar or results. `Services/StudioCompletionSource.cs` bridges snippets +
  schema + the active dialect into whichever surface is mounted.
- **Native chrome**: the window uses a `MicaBackdrop`; the rail, toolbar and status bar are
  transparent so they read as one chrome surface with the title bar, and only real content sits on
  Fluent card brushes (`CardBackgroundFillColorDefaultBrush`). Falls back to the design's solid
  background when `MicaController.IsSupported()` is false.

## Deliberate gaps (next iterations)

| Gap | Why / next step |
|---|---|
| Only two providers ship | SQL Server and Demo. PostgreSQL/MongoDB only need new provider assemblies (see above and `docs/PROVIDERS.md`), not app changes. |
| Completion context is token-based | Subquery aliases and `UPDATE ... FROM alias` targets still need full statement parsing (the Tauri app uses an AST). Join fallbacks for providers without foreign keys stay name heuristics. Keyword/function/table-context lists come from the active dialect (`TSqlDialect` today). |
| Current-statement splitting is lexical | Each provider owns its splitter (`IEditorLanguage`). T-SQL handles strings, identifiers, comments, indented `GO` statements and `GO` batch splitting for F5; dollar-quoted PostgreSQL bodies need `SqlLanguageOptions` extensions. Nested `BEGIN/END` awareness needs the same AST work as completion. |
| Result rows are capped, not paged | The provider stops at 5,000 rows (100,000 ceiling) to bound memory. Paging/continuation would need a stable ordering contract per driver; start there if deep result browsing becomes a requirement. |
| A failing statement stops the rest of its GO batch | SQL Server executes a GO batch natively, so the first server error ends the remaining statements in that batch. F5 already sends each `GO` batch separately; split-and-execute per statement as well (as the Tauri drivers do) if every statement must report independently. |
| `GO n` repeat counts are parsed but not expanded | The splitter accepts `GO 10` and sends the batch once. Expand it in `SplitBatches` if scripts rely on SSMS's repeat behaviour. |
| No autocommit / manual-commit switch, no isolation dropdown | Deliberately mirrors SSMS for SQL Server: each statement autocommits, `BEGIN TRAN`/`COMMIT` are explicit, and isolation is the server default (`SET TRANSACTION ISOLATION LEVEL` remains available per tab). A DBeaver/DataGrip-style manual-commit mode is a product decision, not a SQL-standard requirement. |
| App exit has no transaction prompt | Closing one tab or disconnecting is guarded and rolls back; closing the whole app relies on SQL Server rolling back when the process's sessions end. |
| Folders, command palette, MCP | Ported later; tab reordering, inline rename, pinning and the archive are in place, the remaining organisation features are not. |
| No snippet import | The manager handles built-ins and custom snippets; DBeaver template XML import from the web app is not ported yet. |

## WinUI notes worth remembering

- WinUI has no `DataGrid` and no Monaco; the editor surface is the real porting cost.
- `ListView` with `SelectionMode="None"` gives no `Invoke` pattern, so tabs are activated with
  `ItemClick` — keyboard activation via Enter still needs an explicit handler.
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
- `AppWindow.Resize` takes physical pixels; size in DIPs and scale by `GetDpiForWindow`.
- MVVM Toolkit `[ObservableProperty]` on fields warns (MVVMTK0045) under WinRT/AOT; partial
  properties keep the build warning-free and AOT-ready.
- `winapp ui` synthetic input only reaches the app while it owns the foreground, and Windows blocks
  a background process from stealing focus. The test harness presses a synthetic Alt before
  `SetForegroundWindow` to unlock it; without that, clicks and keys land in whatever window has focus
  (the IDE), while UIA `invoke`/`get-value` still work — a confusing half-passing suite.
