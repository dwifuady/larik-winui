# Developing Larik

Everything a contributor needs beyond the product tour: layout, build, verification, platform quirks
and releases. Repository rules and architecture invariants live in [`../AGENTS.md`](../AGENTS.md);
provider contracts in [`PROVIDERS.md`](PROVIDERS.md); add-on distribution in [`ADDONS.md`](ADDONS.md).

## Project layout

```
Larik.slnx                     solution (app + provider assemblies + tests)
Larik/                         WinUI 3 app: shell, editor surface, completion, views, services
Larik.Data/                    provider contracts, editor-language seam, catalog, Demo provider
Larik.Data.SqlServer/          SQL Server provider on Microsoft.Data.SqlClient
Larik.Data.Postgres/           PostgreSQL provider on Npgsql
Larik.Data.Tests/              xUnit tests (contracts, editor language, providers)
installer/                     Inno Setup per-user installer
.github/workflows/             CI build/test and tag-driven release
ui-tests.ps1                   ordered UI Automation suite (the executable spec)
docs/                          provider/add-on handbooks, feature detail, this file
```

`Larik.Data` is the dependency-free core (`net10.0`, no packages, no WinUI types). The app owns the
UI and composition root; providers own everything engine-specific. The shell never branches on a
provider — `DatabaseProviderCatalog` loads them from referenced assemblies and Plugins folders.

## Build and run

From the repository root:

```powershell
dotnet build Larik.slnx                      # Debug build, all projects, must stay warning-free
dotnet test Larik.Data.Tests\Larik.Data.Tests.csproj
cd Larik; winapp run . --detach --json       # builds, registers a debug identity, launches
```

Requirements: Windows 10 1809+, .NET 10 SDK, WinApp CLI 0.7+ and Developer Mode. Published builds
are self-contained and need no separate .NET install.

`dotnet build -c Release` produces a self-contained WinUI build (trimming is off, so XAML and
x:Bind survive) and needs no extra flags. Day-to-day runs go through `winapp run`; the release
workflow uses `dotnet publish` with `WindowsPackageType=None` for the portable artifacts.

The Store configuration ships only the built-in providers. Generate its manifest first:

```powershell
pwsh scripts/prepare-store-manifest.ps1 `
    -IdentityName '<Partner Center identity>' `
    -Publisher 'CN=<Partner Center publisher>' `
    -Version 0.1.0

dotnet build Larik/Larik.csproj -c Store
```

`-c Store` compiles `LARIK_STORE`, hides the Drivers page and never scans `Plugins`. See
[`STORE.md`](STORE.md).

## Verify

Test in layers, cheapest first: `dotnet build` → `dotnet test` → the **ordered prefix** of the UI
suite that covers your area.

```powershell
cd Larik
powershell -ExecutionPolicy Bypass -File ..\ui-tests.ps1 -Target local
# While iterating, stop at the check that covers the change:
powershell -ExecutionPolicy Bypass -File ..\ui-tests.ps1 -Target local -Through 'editing another workspace'
```

The UI checks are one ordered scenario (they create and reuse workspaces and tabs), so a check
cannot run alone. `-Through` runs everything up to the named check and marks the rest `SKIP`.
Exit codes: `0` green, `1` failure, `2` `-Through` matched nothing. Run the whole suite only for
the final state of a behaviour change.

The suite drives the real window over UI Automation, starting from an empty state and covering:

- onboarding, workspace switching (click and horizontal trackpad swipe), create, edit and delete,
  with deletion archiving the workspace's SQL;
- tabs: open/rename/reorder/drag into groups, group and undo, pin as folder, archive and restore,
  result-tab ordering, per-tab results and scroll positions;
- editing: completion popup and Tab expansion, Ctrl+Enter vs F5 and `GO` batches, highlighting
  and undo, find/replace including replace-all as one undo step;
- completion details: tables, views, schemas, aliases, joins, CTEs, variables, databases,
  procedures and `EXEC` parameters, `USE` switching the tab, `Ctrl+K` schema peek and pin;
- execution: connect, query, Stop, Messages tab, result caps, copy/export, cell preview and
  foreign-key reference previews, a 1,000-row and a wide result;
- sessions: a transaction does not leak between tabs and the `TXN` guard commits/rolls back;
- persistence: the window rectangle and saved state on disk;
- the connection dialog from provider-declared fields, and a real SQL Server round trip
  (`sys.databases`; needs LocalDB or a reachable server, default profile
  `(localdb)\MSSQLLocalDB`).

The suite parks the `%LOCALAPPDATA%\Larik WinUI` SQLite files (database plus WAL sidecars) before
launching and restores them afterwards. Screenshots are artifacts in `ui-test-artifacts/`
(gitignored). The app is packaged, so a second instance cannot launch while one is running; stop
an existing instance first.

## WinUI notes worth remembering

- WinUI has no `DataGrid` and no Monaco; the editor surface is the real porting cost.
- **ContentDialog does not reliably scroll its content.** A tall form clips its last child with no
  scrollbar. Use a `Grid` (auto / star / auto) with your own `ScrollViewer` for the body and pin
  status/actions outside it.
- **Feedback must be visible.** A status line below a long form can be laid out off-screen; Test
  looked broken although it ran. Any async action needs on-screen feedback and a disabled/busy
  state.
- **RichEditBox caret coordinates are text-area relative**; `Popup` offsets are relative to the
  editor control. Add the editor origin and `Padding` when anchoring; keep a real gap below the
  line. Remove the default `ListViewItem` inset so suggestion rows fill the popup.
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
- Mica: transparent chrome (rail, toolbar, status bar), real surfaces on Fluent card brushes,
  solid fallback when `MicaController.IsSupported()` is false.
- MVVM Toolkit `[ObservableProperty]` on fields warns (MVVMTK0045) under WinRT/AOT; partial
  properties keep the build warning-free and AOT-ready.
- `winapp ui` synthetic input only reaches the app while it owns the foreground, and Windows blocks
  a background process from stealing focus. The test harness presses a synthetic Alt before
  `SetForegroundWindow` to unlock it; without that, clicks and keys land in whatever window has focus
  (the IDE), while UIA `invoke`/`get-value` still work — a confusing half-passing suite.
- A packaged (MSIX) run keeps its state in the package's `LocalState`
  (`%LOCALAPPDATA%\Packages\<PackageFamilyName>\LocalState`); an unpackaged run uses the real
  `%LOCALAPPDATA%\Larik WinUI`. State checks must look in both places.

## Releasing

The git tag is the version. Tag a commit and push it; CI builds both architectures as portable
ZIPs and Inno Setup installers, then publishes them with SHA-256 checksums.

```powershell
git tag v0.1.0
git push origin v0.1.0
```

Keep `Version` in `Larik/Larik.csproj` in sync for local builds; the workflow overrides it from the
tag. The release publishes these asset names:

- `Larik-<version>-win-<arch>-setup.exe`
- `Larik-<version>-win-<arch>.zip`
- `SHA256SUMS.txt`

### Auto-update (GitHub edition)

`Services/UpdateService.cs` asks
`https://api.github.com/repos/dwifuady/larik-winui/releases/latest` once per launch, compares the
tag with `AppInfo.Version`, and picks `Larik-<version>-win-<arch>-setup.exe` when an Inno
uninstaller is beside the app, otherwise the portable ZIP. The download goes to
`%LOCALAPPDATA%\Larik WinUI\updates\<version>\` and is checked against `SHA256SUMS.txt`; a complete
download is reused on the next launch. The installer runs with `/DIR=<current folder> /RELAUNCH=1`,
and the portable path extracts the ZIP and starts a `cmd` helper that waits for the app to exit,
copies with `robocopy`, and reopens `Larik.exe`.

Only **unpackaged** builds self-update. `UpdateService.CanSelfUpdate` returns false when the process
has package identity (Store, or `winapp run`) or when the build defines `LARIK_STORE`; the launch
check and the About controls are then skipped. The preference lives in `app_state` as
`check_for_updates` and defaults to on.

To exercise the real feed locally, publish a portable build with a lower version and run it — the
latest release is offered with its real size. Download and checksum work there; leave Install for a
throw-away folder, because it really does replace the running copy. The UI suite runs the packaged
debug identity, so it asserts the opposite side of the gate: no updater is offered there.

### Store package

The Store build is separate from the tag release. Run **Actions → Store package** with a version;
it generates the manifest from Partner Center values, verifies the MSIX identity, publisher and
version, and uploads an unsigned x64 MSIX for manual submission. See [`STORE.md`](STORE.md).
