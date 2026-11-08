# AGENTS.md

Instructions for contributors and coding agents working in this repository. Read this first, then
the task-specific document it points to.

## What this is

Larik SQL Studio, a WinUI 3 / Windows App SDK desktop app ported from the Tauri app in
`C:\dev\larik-sql-studio`. The app talks to databases through provider assemblies; SQL Server ships
as a built-in provider and an in-memory Demo provider exists for sample workspaces and UI tests.

## Build, run, verify

Run from the repository root unless noted.

```powershell
dotnet build Larik.slnx                      # Debug build, all projects, must stay warning-free
dotnet test Larik.Data.Tests\Larik.Data.Tests.csproj
cd Larik; winapp run . --detach --json       # launch the app (registers a debug identity)
cd Larik; powershell -ExecutionPolicy Bypass -File ..\ui-tests.ps1 -Target local
```

`dotnet build -c Release` alone fails with `NETSDK1102` because `Larik.csproj` sets
`PublishTrimmed=true` for non-Debug builds while the build is framework-dependent. Compile Release
with `dotnet build Larik.slnx -c Release -p:PublishTrimmed=false`, or publish through `winapp`
(the supported path).

The UI suite drives the real window over UI Automation and includes a SQL Server LocalDB round trip.
It parks and restores `%LOCALAPPDATA%\Larik` state.

## Architecture in one paragraph

`Larik.Data` (net10.0, zero package references, no WinUI types) owns the contracts:
`IDatabaseProvider`, `IDatabaseConnection`, `QueryResult`, `TableSchema`, `ProviderSetting`, and the
editor-language seam (`IEditorLanguage`, `ISqlDialect`, `SqlLanguage`). `DatabaseProviderCatalog`
discovers provider implementations from referenced assemblies and Plugins folders. `Larik` (WinUI)
owns the shell, editor surface, completion engine, snippet store and UI. `Larik.Data.SqlServer` owns
everything T-SQL: connection/execution, schema introspection and the T-SQL editor language.

## Hard rules

1. **The app never knows a provider.** No `providerId == "..."` or `dialect == "..."` branches in
   the shell, editor, completion or view models. Provider differences come from the contracts.
2. **`Larik.Data` stays dependency-free and WinUI-free.** It targets `net10.0` so any provider
   assembly can reference it.
3. **Contract changes are additive only.** Adding a member requires a default implementation (see
   `IDatabaseProvider.EditorLanguage` and `IDatabaseConnection.ExecuteBatchAsync`). Never rename or
   remove members, and never add positional parameters to contract records — add nullable `init`
   properties instead. Plugins built against older contracts must keep loading.
4. **Editor splitting is provider-owned.** The shell calls `IEditorLanguage.Split` /
   `StatementAt` for Ctrl+Enter and `SplitBatches` for F5. Do not reintroduce a global SQL splitter
   in `Larik`. Execution-side splitting inside a provider's `ExecuteBatchAsync` is a separate
   concern and may stay provider-internal.
5. **SQL Server specifics live in `Larik.Data.SqlServer`.** Identifier quoting, `GO`, bracket/`N`/`@`
   scanning and completion metadata belong to `TSqlDialect`. The shared *default* token keyword set
   lives in `GenericSqlLanguage.TokenKeywords` because the null-language fallback must reproduce the
   app's historical highlighting; `TSqlDialect` reuses it instead of duplicating it.
6. **No reflection in hot paths.** Tokenizing runs on a typing debounce; completion runs while
   typing. Keep both synchronous, allocation-reasonable and free of I/O.
7. **Do not change persisted state shapes casually.** `StudioStore` owns the SQLite schema and has
   a `user_version` migration path. New provider data goes in `ConnectionProfile.Values`.
8. Follow the existing style: file-scoped namespaces, nullable enabled, `[ObservableProperty]` on
   **partial properties** (not fields — MVVMTK0045 under WinRT/AOT), comments that explain *why*.

## Where things live

| Task | Start here |
|---|---|
| Add a database provider | `docs/PROVIDERS.md` |
| Understand the editor seam | `Larik/Editor/IEditorSurface.cs`, `Larik.Data/EditorLanguage.cs` |
| Change highlighting/splitting | `Larik.Data/SqlLanguage.cs`, provider dialect |
| Change completion | `Larik/Services/CompletionEngine.cs`, `StudioCompletionSource.cs` |
| Connection UI | `ProviderSetting` in `Larik.Data/Contracts.cs`, `MainPage.xaml.cs` dialog |
| Persistence | `Larik/Services/StudioStore.cs` |
| Secrets | `Larik/Services/SecretStore.cs` (Windows Credential Manager) |
| Shell/UI | `Larik/MainPage.xaml(.cs)`, `Larik/ViewModels/*` |
| UI regression suite | `ui-tests.ps1` |

## Adding a provider: the short version

1. New assembly targeting `net10.0`, referencing `Larik.Data` (and the client library, e.g. Npgsql).
2. Implement `IDatabaseProvider` (Id, DisplayName, Dialect, Settings, CreateConnection).
3. Implement `IDatabaseConnection` (open/close, databases, execute/batch, schema, row caps).
4. Implement an editor language: `ISqlDialect` + `SqlLanguage` for SQL engines, or
   `IEditorLanguage` for non-SQL engines (MongoDB). Return it from
   `IDatabaseProvider.EditorLanguage`.
5. Add tests (split/token/schema contract) and register the assembly as a built-in **or** drop it
   in a Plugins folder.

Full detail, including PostgreSQL and MongoDB blueprints and the current plugin-loader limitations:
[`docs/PROVIDERS.md`](docs/PROVIDERS.md).

## Verify your change

- `dotnet build Larik.slnx` — zero warnings.
- `dotnet test Larik.Data.Tests\Larik.Data.Tests.csproj` — editor-language golden tests.
- If you touched the editor, run `ui-tests.ps1 -Target local` (Ctrl+Enter, F5, completion,
  highlighting + undo).
- If you touched a provider, run its integration check against a real server where possible.

## Known honest gaps

Read the table at the end of `docs/PROVIDERS.md`. Notably: completion is still SQL-clause-shaped
and schema-unqualified, SnippetDialects is a fixed list, the plugin loader has no
`AssemblyLoadContext` isolation, and there is no non-SQL completion contributor yet.
