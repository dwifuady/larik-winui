# Larik — WinUI 3 port

A native WinUI 3 / Windows App SDK shell for Larik SQL Studio, ported from the Tauri + React app,
now talking to **real databases through pluggable providers**:
**Arc-style vertical tabs, workspaces that group databases, and snippet completion that expands on Tab.**

```
C:\dev\larik-winui\
├── Larik.slnx                    # solution (app + provider assemblies)
├── Larik.Data/                   # provider contracts + catalog + Demo provider (net10.0)
│   ├── Contracts.cs              # IDatabaseProvider, IDatabaseConnection, QueryResult, settings schema
│   ├── DatabaseProviderCatalog.cs# discovers providers from assemblies + Plugins folders
│   └── Demo/DemoProvider.cs      # in-memory provider (sample workspaces + UI tests)
├── Larik.Data.SqlServer/         # SQL Server provider on Microsoft.Data.SqlClient
├── Larik/                        # WinUI 3 app (net10.0-windows10.0.26100.0, WASDK 2.6)
│   ├── Themes/StudioTheme.xaml   # DESIGN.md tokens as Light/Dark/HighContrast brushes
│   ├── Models/StudioModels.cs    # Snippet, CompletionItem
│   ├── Services/
│   │   ├── SnippetCatalog.cs     # built-in snippets ported 1:1 from src-tauri/src/storage/snippets.rs
│   │   ├── CompletionEngine.cs   # clause-aware completion, ${cursor}/${1:placeholder} expansion
│   │   ├── SqlFormatter.cs       # conservative keyword/clause formatter
│   │   ├── StudioStore.cs        # SQLite session store: spaces, tabs, app_state (no passwords)
│   │   └── StudioColors.cs       # space palette + active-tab tint
│   ├── ViewModels/               # Studio / Space / Connection / Database / Tab
│   ├── Editor/IEditorSurface.cs  # the editor seam: text, caret, completion source, shell commands
│   ├── Controls/SqlEditorView.*  # IEditorSurface impl #1: RichEditBox + caret-anchored popup
│   ├── Controls/DragHandle.cs    # splitter with a resize cursor
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
cd C:\dev\larik-winui\Larik
powershell -ExecutionPolicy Bypass -File ..\ui-tests.ps1 -Target local
```

The suite drives the real window over UI Automation: tab switching, `sel` → suggestion popup →
Tab accepts the expansion and lands the caret after `FROM `, Ctrl+Enter runs through the provider,
`FROM ` lists every table in the database, workspace switching swaps connection + tabs + editor, a
workspace starts disconnected until the status-bar action connects it, the database picker repoints
the rail/status bar/tab subtitle, the connection dialog renders the provider's declared fields and
its Test result, saved state is written to disk, and a real **SQL Server** workspace is
connected, its databases listed and `sys.databases` queried (that check needs SQL Server LocalDB
or a reachable server; it is the seeded `(localdb)\MSSQLLocalDB` profile).

The suite parks the `%LOCALAPPDATA%\Larik` SQLite files (database + WAL sidecars) before
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

The app never references a specific provider. `DatabaseProviderCatalog` finds implementations in
the app's referenced provider assemblies plus every DLL dropped into:

- `<app dir>\Plugins`
- `%LOCALAPPDATA%\Larik\Plugins` (where user-installed plugins belong; the MSIX install folder is read-only)

The connection dialog builds its form from `ProviderSetting` (text/password/number/boolean), so a
new provider gets a working configuration UI with no app changes. Providers are trusted code:
only drop DLLs you have built or reviewed. Load failures are reported in the dialog instead of
crashing the app.

Shipped today: `Larik.Data.SqlServer` (Microsoft.Data.SqlClient, integrated or SQL auth,
`sys.databases` for the picker, `sys.tables`/`sys.columns` for completion) and
`Larik.Data.Demo` (in-memory, used by the sample workspaces).

## What works in this slice

- **Vertical tabs** per workspace, active tab tinted with the workspace colour (2 px strip + 26 % tint),
  database subtitle revealed on hover/active, close button, Ctrl+T for a new query.
- **Workspaces, connections and databases**: one space owns exactly one connection; that
  connection exposes several databases. The rail's top strip carries the workspace switcher, whose
  chip shows the workspace name and its connection state (dot **and** `Connected`/`Disconnected`) —
  **spaces start disconnected**. Connect from the chip's status bar action or the workspace menu;
  running a query while disconnected is refused with an explicit message. The row beneath the strip
  is a database picker for the active connection, filled from the provider once connected. Switching
  database repoints the rail, the toolbar badge, the status bar, the active tab's subtitle, the
  snippet dialect and the completion schema.
- **Real execution**: connect opens the provider connection, `Run`/Ctrl+Enter executes there and the
  grid renders returned columns/rows (NULLs dimmed, numerics right-aligned). Errors come back from
  the provider with timing.
- **Snippet completion**: type a trigger prefix, get a popup anchored to the caret via
  `ITextSelection.GetRect` (text-area coordinates, offset by the editor's padding), Up/Down to move,
  **Tab or Enter accepts**, Esc dismisses. Expansion honours `${cursor}`, `${1:placeholder}` (left
  selected), repeat markers, and `alias.`/`table.` column completion resolved through the
  statement's aliases. Completion is clause-aware: after `FROM`/`JOIN`/`INTO`/`UPDATE`/`TABLE` it
  lists every table in the active database, after `SELECT`/`WHERE`/`ON`/`AND`/`SET`/... the columns
  of the tables in that statement, and elsewhere snippets + keywords + schema.
- **Persistence**: a SQLite database at `%LOCALAPPDATA%\Larik\larik.db` (`larik-dev.db` in Debug
  builds, like the Tauri version) keeps `spaces`, `space_settings`, `tabs` and `app_state` — the
  same model as `src-tauri/src/storage` — so spaces, connection profiles (non-secret settings),
  tabs and their SQL survive a restart. WAL mode, foreign keys with cascade deletes, schema
  versioned via `PRAGMA user_version`; a pre-SQLite `studio.json` is imported once. Spaces still
  start disconnected; provider-declared password fields are never written, so SQL-auth
  connections re-prompt after a restart. Corrupt state falls back to the seeded spaces.
- **Results grid**: ListView-free, code-generated columns/rows (500-row cap), numeric columns
  right-aligned, space-coloured rule under the header.
- **Design system**: DESIGN.md tokens, Light/Dark/HighContrast dictionaries, mono editor,
  status bar with connection + `Ln/Col`, splitter between editor and results.
- **Editor seam**: the shell never touches a `RichEditBox`. It drives `IEditorSurface`
  (text, caret, `CompletionSource`, `TextChanged`/`CaretChanged`/`CommandRequested`, `FocusEditor`),
  so the editor can be swapped for a WebView2/Monaco island without changing the rail, tabs,
  spaces, toolbar or results. `Services/StudioCompletionSource.cs` bridges snippets + schema into
  whichever surface is mounted.
- **Native chrome**: the window uses a `MicaBackdrop`; the rail, toolbar and status bar are
  transparent so they read as one chrome surface with the title bar, and only real content sits on
  Fluent card brushes (`CardBackgroundFillColorDefaultBrush`). Falls back to the design's solid
  background when `MicaController.IsSupported()` is false.

## Deliberate gaps (next iterations)

| Gap | Why / next step |
|---|---|
| **No syntax highlighting** | `RichEditBox` is plain text here. Next: format ranges on a debounce (`ITextCharacterFormat`), or host Monaco in WebView2 and keep the native shell. |
| **Secrets are memory-only** | Connection profiles persist without provider-declared password fields. Next: Windows Credential Manager (what `keyring` uses in the Tauri app) or DPAPI, instead of keeping them in memory. |
| Only two providers ship | SQL Server and Demo. PostgreSQL/MongoDB are drop-in assemblies (see above), not app changes. |
| No query cancellation UI | `Run` disables while a statement is in flight; the provider already accepts a `CancellationToken`. |
| Results grid not virtualized | Fine for a slice; for large result sets move to `ItemsRepeater` with a shared row template. |
| No drag-reorder, folders, pinned tabs, command palette, MCP | Ported later; the tab list is a plain `ListView` so reorder is a small change. |
| No snippet editor UI | Catalog is code-defined; the web app's `SnippetsDialog` has no counterpart yet. |

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
- `AppWindow.Resize` takes physical pixels; size in DIPs and scale by `GetDpiForWindow`.
- MVVM Toolkit `[ObservableProperty]` on fields warns (MVVMTK0045) under WinRT/AOT; partial
  properties keep the build warning-free and AOT-ready.
- `winapp ui` synthetic input only reaches the app while it owns the foreground, and Windows blocks
  a background process from stealing focus. The test harness presses a synthetic Alt before
  `SetForegroundWindow` to unlock it; without that, clicks and keys land in whatever window has focus
  (the IDE), while UIA `invoke`/`get-value` still work — a confusing half-passing suite.
