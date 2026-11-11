# Larik — native WinUI 3 SQL studio

A native WinUI 3 / Windows App SDK SQL studio, ported from the Tauri + React app. It talks to real
databases through pluggable providers: **Arc-style vertical tabs and workspaces, live SQL syntax
highlighting, and snippet completion that expands on Tab.**

![WinUI 3](https://img.shields.io/badge/WinUI-3-0E7A0D) ![.NET](https://img.shields.io/badge/.NET-10-512BD4) ![License](https://img.shields.io/badge/license-MIT-blue)

## Download

Published for `win-x64` and `win-arm64` from
[Releases](https://github.com/dwifuady/larik-winui/releases):

- **Installer** — `Larik-<version>-win-<arch>-setup.exe`. Per-user install to
  `%LOCALAPPDATA%\Programs\Larik`, no admin rights, a **Larik** Start menu entry,
  and it uninstalls from **Apps & Features**. It installs and runs beside the
  Tauri app, which keeps the name *Larik SQL Studio*.
- **Portable** — `Larik-<version>-win-<arch>.zip`. Unpack anywhere and run
  `Larik.exe`. Both GitHub builds keep their data in `%LOCALAPPDATA%\Larik WinUI`.

### Editions

- **GitHub** — the installer and portable ZIP above, with Settings → Drivers and the curated
  index (MongoDB today). These binaries are unsigned.
- **Microsoft Store** — SQL Server + PostgreSQL built in, signed by Microsoft, and no add-on
  surface. The Store build keeps its data in the package's local store; the listing is being
  prepared. See [`docs/STORE.md`](docs/STORE.md).

Requires Windows 10 1809+ and does not need a separate .NET install. The GitHub
installer and portable ZIP are unsigned, so Windows SmartScreen may warn on the first run:
**More info → Run anyway**. The Microsoft Store edition is signed by Microsoft.

GitHub builds keep themselves current: an in-app notice checks Releases at launch, downloads the
matching installer or portable ZIP, verifies its checksum, and installs it without leaving the app.
The Store edition updates through the Store instead.

## What it does

- **Workspaces are the object.** One workspace owns one connection; its databases sit underneath.
  Workspaces start disconnected and connect from the rail, wear a colour and Fluent icon, and can
  be hidden from the rail or reordered. Deleting one archives its SQL first.
- **Arc-style tabs.** Vertical query tabs with collapsible groups, pinned folders, drag reorder,
  inline rename, and non-destructive close into an archive with optional auto-archive.
- **A real editor.** Provider-driven syntax highlighting, find/replace, snippet expansion on Tab,
  and clause-aware completion for tables, columns, aliases, joins, CTEs, variables, databases and
  procedure calls. `Ctrl+K` opens a schema peek that pins beside the editor.
- **Real execution.** `Ctrl+Enter` runs the statement under the caret, `F5` runs the document or
  selection and splits `GO` batches, and `Stop` cancels without closing the session. `PRINT` and
  warnings open a Messages tab.
- **One session per tab.** Temp tables, `SET` options and `BEGIN TRAN` stay on that tab; a `TXN`
  guard offers Commit and Roll back, and `USE` moves the tab, rail and completion to the new
  database.
- **Results that hold up.** One result tab per result set, a virtualized grid, cell preview with
  JSON/XML formatting, foreign-key reference previews, TSV copy, and CSV/JSON export.
- **Windows-native craft.** Mica and the system theme, Light/Dark/HighContrast, real keyboard and
  accessibility support, and no web-app chrome.

The detailed behaviour of every surface — and the honest list of deliberate gaps — lives in
[`docs/FEATURES.md`](docs/FEATURES.md).

## Requirements

- Windows 10 1809+ (`x64` or `arm64`).
- To build from source: .NET 10 SDK, [WinApp CLI](https://github.com/microsoft/winappCli)
  0.7+, and Developer Mode. Published builds are self-contained and need no
  separate .NET install.
- SQL Server and PostgreSQL wherever you use them; MongoDB is an add-on in the GitHub edition.
  SQL Server LocalDB is enough, and the in-memory Demo provider works offline with no server at
  all.

## Build from source

```powershell
cd Larik
winapp run . --detach --json     # builds, registers a debug identity, launches
```

```powershell
dotnet build Larik.slnx
dotnet test Larik.Data.Tests\Larik.Data.Tests.csproj
cd Larik
powershell -ExecutionPolicy Bypass -File ..\ui-tests.ps1 -Target local
```

Build, test, the ordered `-Through` loop for the UI suite, project layout, WinUI notes and the
release process are in [`docs/DEVELOPING.md`](docs/DEVELOPING.md).

## Providers

A provider is an assembly that implements `Larik.Data.IDatabaseProvider`. It owns connections,
execution, schema metadata **and** its editor language (tokenizing, statement/batch splitting and
completion lists), so the shell never knows a specific database.

- **SQL Server** (built in, Microsoft.Data.SqlClient) — the default profile is
  `(localdb)\MSSQLLocalDB`.
- **PostgreSQL** (bundled, Npgsql).
- **Demo** (built in, in-memory) — offline use, onboarding and UI tests.
- **MongoDB** — installed as a driver/add-on from Settings → Drivers.

Providers are discovered from the app's referenced assemblies and from `Plugins` folders under the
app directory and `%LOCALAPPDATA%\Larik WinUI`. The connection dialog is generated from the
provider's declared settings, and declared password fields go to Windows Credential Manager.

The contract, compatibility rules and provider blueprints are in
[`docs/PROVIDERS.md`](docs/PROVIDERS.md). Add-on packaging, the curated index and Store channels are
in [`docs/ADDONS.md`](docs/ADDONS.md).

## Documentation

| Document | Contents |
|---|---|
| [`docs/FEATURES.md`](docs/FEATURES.md) | Every surface in detail, plus known gaps and next steps |
| [`docs/DEVELOPING.md`](docs/DEVELOPING.md) | Layout, build, verification, WinUI quirks, releasing |
| [`docs/PROVIDERS.md`](docs/PROVIDERS.md) | Provider contract, editor-language seam, blueprints |
| [`docs/ADDONS.md`](docs/ADDONS.md) | Add-on index, packaging, loading, Store channels |
| [`docs/STORE.md`](docs/STORE.md) | Store edition, manifest identity and submission checklist |
| [`AGENTS.md`](AGENTS.md) | Architecture invariants and repository rules |
| [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) | Bundled components and licenses |

## License

Larik is free software under the **MIT License** (see `LICENSE`).
Bundled components and their licenses are listed in
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
