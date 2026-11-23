# Larik — Rust + GPUI Kit Proof of Concept

A self-contained proof of concept rewriting Larik's core vertical slice in Rust using
**GPUI Kit** (`gpui-kit` — Zed's GPU-accelerated GPUI engine + `gpui-component` + `tree-sitter-sql`)
and **`tiberius`** for async SQL Server connectivity.

## Workspace Layout

```text
poc-rust/
├── Cargo.toml
└── crates/
    ├── larik-data/            # Provider-agnostic traits, QueryResult, TableSchema, SQL splitter & completion engine
    ├── larik-provider-demo/   # Offline in-memory Demo provider (customers, orders, order_items, products)
    ├── larik-provider-mssql/  # Real SQL Server provider via tiberius (TCP, named instances, Windows auth, LocalDB)
    ├── larik-core/            # StudioState, isolated SQLite store, Tokio per-tab SessionManager
    └── larik-ui/              # GPUI Kit desktop shell (Arc rail, Tree-sitter SQL Editor, virtualized DataTable)
```

## Run & Test

```powershell
cd poc-rust
cargo test --workspace
cargo run -p larik-ui
```

> State is stored in `%LOCALAPPDATA%\Larik Rust PoC\studio.db` so it never touches the WinUI app's
> `%LOCALAPPDATA%\Larik WinUI` database.

## Implemented PoC Vertical Slice

- **Arc-style left rail**: workspaces with colour + initial badge, workspace switcher dots at the bottom,
  text + colour connection status, Connect/Disconnect, database switcher (accessible + inaccessible states),
  vertical query tabs with 2 px active strip, inline rename, and non-destructive close into Archive with Restore.
- **Provider-generated Connection Dialog**: renders fields from `DatabaseProvider::settings()` (Demo or SQL Server),
  supports workspace name/colour editing, collapsible Advanced options, inline **Test connection**, and **Save & Connect**.
- **Tree-sitter SQL Code Editor**: powered by `gpui-component`'s rope-backed `Editor` with `tree-sitter-sql` syntax
  highlighting, line numbers, built-in `Ctrl+F` search, undo/redo, and a live schema/alias-aware `CompletionProvider`
  (`Ctrl+Space` or typing `.` / identifiers).
- **Per-tab async execution & Virtualized Results Grid**:
  - `Ctrl+Enter` runs the statement under the caret (or selection); `F5` runs the full document / `GO` batches.
  - Each tab owns its own `DatabaseConnection` session on a Tokio runtime, with live **Stop** cancellation.
  - Multi-result-set tabs + **Messages** tab + **Copy TSV**.
  - Virtualized `DataTable` with fixed sticky `#` row-number gutter, resizable & draggable columns, striped rows,
    and italic `NULL` rendering.
- **Schema Peek Drawer (`Ctrl+K`)**: browse tables/views, columns, data types, and `PK`/`FK` badges; click to insert
  column names or `SELECT TOP 100`.
- **Dark & Light Themes (`Ctrl+Shift+L`)**: styled with tokens ported from `Larik/Themes/StudioTheme.xaml`.
