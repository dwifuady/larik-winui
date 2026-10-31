# Larik — WinUI 3 port (UI vertical slice)

A native WinUI 3 / Windows App SDK shell for Larik SQL Studio, ported from the Tauri + React app.
This first slice is about the part of the product the web stack gives away for free and WinUI does not:
**Arc-style vertical tabs, workspaces that group databases, and snippet completion that expands on Tab.**

```
C:\dev\larik-winui\
├── Larik/                  # WinUI 3 app (C#, net10.0-windows10.0.26100.0, WinUI 3 / WASDK 2.6)
│   ├── Themes/StudioTheme.xaml   # DESIGN.md tokens as Light/Dark/HighContrast brushes
│   ├── Models/StudioModels.cs    # Snippet, CompletionItem, TableDef, ResultSet
│   ├── Services/
│   │   ├── SnippetCatalog.cs     # built-in snippets ported 1:1 from src-tauri/src/storage/snippets.rs
│   │   ├── CompletionEngine.cs   # trigger matching, ${cursor}/${1:placeholder} expansion, alias.column
│   │   ├── SqlFormatter.cs       # conservative keyword/clause formatter
│   │   ├── MockDatabase.cs       # stand-in for tiberius/postgres/rusqlite
│   │   └── StudioColors.cs       # space palette + active-tab tint
│   ├── ViewModels/               # Studio / Space / Database / Tab (CommunityToolkit.Mvvm, partial properties)
│   ├── Controls/DragHandle.cs    # splitter with a resize cursor
│   └── MainPage.xaml(.cs)        # shell layout + caret-anchored completion popup + results grid
└── ui-tests.ps1            # runnable UI Automation checks for the flows above
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
Tab accepts the expansion and lands the caret after `FROM `, Ctrl+Enter runs and fills the grid,
workspace switching swaps databases + tabs + editor, the database picker repoints the rail, status
bar and tab subtitle, adding a database works, Ctrl+B collapses/restores the sidebar, and a
`DELETE`/`UPDATE` without `WHERE` is refused.

## What works in this slice

- **Vertical tabs** per workspace, active tab tinted with the workspace colour (2 px strip + 26 % tint),
  database subtitle revealed on hover/active, close button, Ctrl+T for a new query.
- **Workspaces and databases**: the rail's top strip carries the workspace switcher (colour dot,
  name, menu of workspaces with their database counts); the row beneath it is a database picker
  for the active workspace, with per-database status (colour *and* text) and a `New database`
  action. Switching database repoints the rail, the status bar, the toolbar badge and the active
  tab's subtitle.
- **Snippet completion**: type a trigger prefix, get a popup anchored to the caret via
  `ITextSelection.GetRect`; Up/Down to move, **Tab or Enter accepts**, Esc dismisses. Expansion
  honours `${cursor}`, `${1:placeholder}` (left selected), repeat markers, and `alias.` column
  completion from the schema. Keywords, tables and columns are suggested alongside snippets.
- **Results grid**: ListView-free, code-generated columns/rows (500-row cap), numeric columns
  right-aligned, space-coloured rule under the header.
- **Design system**: DESIGN.md tokens, Light/Dark/HighContrast dictionaries, mono editor,
  status bar with connection + `Ln/Col`, splitter between editor and results.
- **Native chrome**: the window uses a `MicaBackdrop`; the rail, toolbar and status bar are
  transparent so they read as one chrome surface with the title bar, and only real content sits on
  Fluent card brushes (`CardBackgroundFillColorDefaultBrush`). Falls back to the design's solid
  background when `MicaController.IsSupported()` is false.

## Deliberate gaps (next iterations)

| Gap | Why / next step |
|---|---|
| **No syntax highlighting** | `RichEditBox` is plain text here. Next: format ranges on a debounce (`ITextCharacterFormat`), or host Monaco in WebView2 and keep the native shell. |
| No persistence | Spaces, databases and tabs are seeded in `StudioViewModel.Seed()`. Next: SQLite (`Microsoft.Data.Sqlite`) with the same shape as `src-tauri/src/storage`. |
| Mock connections | `MockDatabase` returns deterministic rows and ignores which database is selected. Next: `Microsoft.Data.SqlClient` / `Npgsql` behind a `IDatabaseDriver` keyed by database. |
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
