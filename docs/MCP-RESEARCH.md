# MCP support for Larik SQL Studio (WinUI) — research and design

Research document, 2026-11-12. No product code was changed. Sources: this repository
(`C:\dev\larik-winui`), the Tauri reference (`C:\dev\larik-sql-studio`), the MCP specification
pages at modelcontextprotocol.io, nuget.org, and the csharp-sdk GitHub releases — URLs cited
inline. Anything not confirmed this way is marked **UNVERIFIED**.

---

## 1. TL;DR

- **Yes, MCP fits the WinUI app** — and the honest parity surface is *larger* than Tauri's, because a
  separate stdio EXE works **headless** (app closed), which Tauri explicitly rejected for v1.
- **Recommended architecture: a dedicated `Larik.Mcp` console EXE speaking stdio** (spawned by the MCP
  client), plus an optional **named-pipe channel back to the running app** for approval dialogs,
  toasts and "open this query as a tab". No HTTP listener, no port, no bearer token in the app.
- **Transport: MCP stdio** via the official **`ModelContextProtocol` C# SDK 2.4.0** (stable,
  2026-11-07; 2.0.0 aligned with the 2026-07-28 spec revision). stdio-only needs **no ASP.NET
  Core**: the main package depends only on `ModelContextProtocol.Core`,
  `Microsoft.Extensions.Caching.Abstractions` and `Microsoft.Extensions.Hosting.Abstractions`.
- **Packaging**: the EXE ships *inside the existing MSIX* next to `Larik.exe` with a
  **`larik-mcp` execution alias** (`desktop4:Subsystem="console"`); the GitHub portable build uses
  the plain EXE path. Heavy dependencies (SqlClient 7.1.1, Npgsql, Sqlite) are already in the
  package for the providers; the MCP SDK adds roughly a few MB.
- **Security model**: global switch (default off) + per-space tri-state `off/read/write` (default
  off), fail-closed provider-owned read-only enforcement, human approval for writes, full audit in
  the same SQLite store. Three user decisions disappear vs Tauri: **no port, no token, no
  token-regenerate**.
- **Effort**: ~10–15 working days over 3 phases after a 1–2 day spike (execution-alias + stdio
  round-trip on a packaged build, shared-runtime publish layout, Store-manifest survival).
- **Top risks**: (1) execution-alias stdio forwarding must be smoke-tested per client (works from
  Win32 parents; Cygwin/MinTTY had a client-side bug fixed in msys2-runtime 3.6.x); (2) sharing one
  self-contained runtime folder between two apphosts needs a publish spike; (3) MRTR/elicitation
  support outside Claude Code is uneven — the app-side approval dialog remains the fallback that
  always works.

---

## 2. What Tauri ships today

Measured with `Measure-Object -Line` (non-blank lines) on `C:\dev\larik-sql-studio`, 2026-11-12.

### Backend (`src-tauri`)

| File | LOC | What it is |
|---|---:|---|
| `src/mcp/mod.rs` | 144 | `McpState` (shared `Arc`s into the app's managers), `McpSupervisor` start/stop on toggle |
| `src/mcp/server.rs` | 365 | Hand-rolled JSON-RPC 2.0 over axum `POST /mcp` (spec's JSON response mode), constant-time bearer check, 1 MiB body cap, `initialize`/`ping`/`tools/list`/`tools/call`, notifications → 202, GET/DELETE → 405. ~150 LOC are raw-socket tests |
| `src/mcp/policy.rs` | 534 | Per-space `Access` tri-state (fail-closed parse), MSSQL fail-closed classifier (strip literals → split `;` → read-verb lead + banned-token list + `xp_`/`sp_`), PostgreSQL gate (one statement per unit, banned session/transaction leads + escape hatches), MongoDB shell allow-list + `$out`/`$merge` denial. ~200 LOC are tests |
| `src/mcp/tools.rs` | 564 | 4 tools, connection resolution (lazy connect), credential redaction (`redact_host`), row clamp 200/1000, 120 s statement timeout, write approval gate, audit write per call |
| `src/mcp/approvals.rs` | 308 | Write-approval queue: park + oneshot wait, 2-minute auto-deny, toast + taskbar flash, persisted lifecycle (pending/approved/denied/timeout/abandoned/orphaned) |
| `src/storage/mcp_log.rs` | 319 | `mcp_log` + `mcp_approvals` tables, insert/list/status-update helpers, 10 k statement cap |
| `src/storage/database.rs` (MCP parts) | ~60 | `mcp_log`, `mcp_approvals` DDL; `mcp_access` column migration |
| `src/commands.rs` / `lib.rs` / `state.rs` (MCP parts) | ~100 | 7 Tauri commands (get/set enabled, set port, regenerate token, get log, respond/get approvals), supervisor wiring, settings accessors |

Backend total ≈ **1,915 LOC in `mcp/` + ~480 storage/commands glue**.

### Frontend (`src`)

| Surface | Size | What it is |
|---|---|---|
| `components/McpApprovalDialog.tsx` | 97 (whole file) | Modal queue: oldest pending request, statement preview, Approve/Deny, rehydrate on mount |
| `components/SettingsDialog.tsx` MCP section | 88 of 653 lines mention MCP | Enable toggle, port, token reveal/copy, `claude mcp add` snippet, audit log view |
| `api/index.ts` + `types/index.ts` | 24 + 16 MCP-mentioning lines | 7 invoke wrappers + `McpSettings`, `McpLogEntry`, `McpApprovalRequest` |
| `CreateSpaceModal.tsx`, `SpacesSelector.tsx`, `SpaceSwitcher.tsx`, `App.tsx` | 12 + 11 + 11 + 2 | Per-space tri-state selector + dialog mounting |

Frontend total ≈ **330 LOC**. Whole feature ≈ **2,600 LOC**, of which ~500 are tests.

### Security model as implemented (with the gaps it admits)

| Property | Implementation | Admitted gap |
|---|---|---|
| Off by default | `mcp_enabled` app setting + `mcp_access` column, both default off; server binds nothing unless enabled | — |
| Network | `127.0.0.1:7487` only, no CORS, loopback → no firewall prompt | Loopback + token is still a port any local process can probe |
| Auth | 32-hex bearer token, constant-time compare, 401 without detail, regenerate button | Token lives in the client's config file (`~/.claude.json`) — user-readable, copyable |
| Read enforcement (PG) | Engine-level `default_transaction_read_only=on` on a dedicated session + statement gate | Strong |
| Read enforcement (MSSQL) | **Fail-closed classifier only** — MCP_DESIGN.md calls it "best-effort"; hard guarantee requires a read-only login | Misclassified exotic T-SQL runs with the saved login's permissions |
| Read enforcement (Mongo) | Shell allow-list + write-stage denial | Strong for parsed statements; SQL subset "read-only by construction" |
| Writes | Every `run_query` on a `write` space parks for an in-app approval (2 min auto-deny, toast + taskbar flash), 10 k review cap | Requires the GUI app to be running — the server lives inside it |
| Audit | Every call incl. denials → `mcp_log`; approval lifecycle → `mcp_approvals`; decision latency split from DB time | Statement text persisted up to 10 k chars (deliberate) |
| Limits | 200 default / 1000 max rows, 120 s timeout, ~1 MiB response guard | — |
| Redaction | MongoDB connection-string userinfo stripped from `list_connections` | Mongo URIs stay in SQLite at rest (schema limitation, documented) |

Explicit non-goals in Tauri: resources, prompts, sampling, elicitation, LAN/OAuth, and — decisively —
**"A headless stdio sibling binary (rejected for v1; HTTP-in-GUI chosen)"**.

---

## 3. MCP in 2026: spec + C# SDK facts

### 3.1 Protocol revisions (verified)

Current revision is **2026-07-28** (fetched from modelcontextprotocol.io — the spec's own schema
path is `schema/2026-07-28/schema.ts`). History that matters to us:

| Revision | What it introduced |
|---|---|
| 2024-11-05 | Baseline (initialize handshake, stdio, HTTP+SSE) |
| 2025-03-26 | **Tool annotations** (`readOnlyHint` etc.) |
| 2025-06-18 | Elicitation, structured tool content, Streamable HTTP |
| 2025-11-25 | URL-mode elicitation |
| 2026-07-28 | "Modern" era (current): per-request `_meta` (version + client capabilities), `server/discover`, `UnsupportedProtocolVersionError` (-32022), **Multi-Round-Trip Requests (MRTR)** replace server-initiated requests, sessions removed, tools/list pagination + caching hints, roots/sampling/logging deprecated |

Key 2026-07-28 facts (from the pages fetched below):

- **No `initialize` handshake.** Every request carries protocol version and client capabilities in
  `_meta.io.modelcontextprotocol/*`. Servers must implement `server/discover`. A **dual-era** server
  answers legacy `initialize` requests for older clients; the C# SDK does this automatically.
- **Transports**: **stdio** (newline-delimited JSON-RPC over the client-launched subprocess's stdin/
  stdout; stderr for logs; shutdown = client closes stdin; client restarts crashed servers) and
  **Streamable HTTP** (POST + JSON or SSE reply). HTTP+SSE is legacy. Custom transports over reliable
  byte streams should reuse stdio framing.
- **Servers no longer send JSON-RPC requests.** To ask a human something mid-tool-call, the server
  returns `resultType: "input_required"` — an `InputRequiredResult` carrying `elicitation/create`
  params (`mode: "form"`, `message`, restricted flat JSON `requestedSchema`) plus `requestState`;
  the client renders the form and **retries the same `tools/call`** with `inputResponses`
  (`action: accept/decline/cancel`). This is MRTR — the mechanism our write approvals will use when
  the app is closed.
- **Tool annotations** are unchanged from 2025-03-26: `title`, `readOnlyHint`, `destructiveHint`,
  `idempotentHint`, `openWorldHint` — all hints, pessimistic defaults (unannotated tool = writable,
  destructive, open-world). Clients use them for pre-approval/confirmation UX.
- **Tools**: `tools/list` supports pagination (`cursor`, `ttlMs`, `cacheScope`) and SHOULD be
  deterministic; `tools/call` supports structured content + `outputSchema`; tool errors an agent can
  act on go in `isError: true` results; long-running work is the **Tasks extension** (opt-in, not
  needed for v1).
- **Local stdio trust boundary**: no authorization spec applies to stdio (OAuth is HTTP-only); the
  trust model is the OS user who spawned the process.

### 3.2 C# SDK (verified)

From nuget.org and github.com/modelcontextprotocol/csharp-sdk (fetched 2026-11-12):

- **`ModelContextProtocol` current stable: 2.4.0** (published 2026-11-07). 2.0.0 (2026-07-28, same
  day as the spec revision) is the "stable alignment with MCP 2026-07-28" release; 1.0.0 was
  2026-02-25. 52.5M total downloads; used by Aspire, Semantic Kernel, ABP, Uno.
- **Packages**: `ModelContextProtocol.Core` (minimal deps), **`ModelContextProtocol`** (hosting + DI
  + attribute discovery; *the right fit for stdio servers*), `ModelContextProtocol.AspNetCore`
  (HTTP), `ModelContextProtocol.Extensions.{Apps,Skills,Tasks}`. The stdio package's only
  dependencies are `ModelContextProtocol.Core`, `Microsoft.Extensions.Caching.Abstractions`,
  `Microsoft.Extensions.Hosting.Abstractions` — **no ASP.NET Core, no Kestrel** for a stdio server.
  Targets net8.0/net9.0/net10.0/netstandard2.0; nupkg is 222.69 KB.
- **Server hosting pattern** (from the official getting-started):

  ```csharp
  var builder = Host.CreateApplicationBuilder(args);
  builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
  builder.Services
      .AddMcpServer()
      .WithStdioServerTransport()
      .WithToolsFromAssembly();
  await builder.Build().RunAsync();

  [McpServerToolType]
  public static class EchoTool
  {
      [McpServerTool, Description("Echoes the message back.")]
      public static string Echo(string message) => $"hello {message}";
  }
  ```

  HTTP servers instead use `ModelContextProtocol.AspNetCore` + `AddMcpServer().WithHttpTransport(...)`
  + `MapMcp()` (with `Stateless = true` the recommended default).
- **2.0.0 verified highlights** (release notes): discovery-first negotiation with automatic fallback
  to the legacy `initialize` handshake for down-level peers (i.e. **dual-era for free**), **MRTR
  added (#1458) and stabilized (#1642)**, stateless HTTP by default, caching hints, roots/sampling/
  logging deprecated (MCP9005 warnings, ~12-month window), NativeAOT in CI coverage. The exact C#
  API shape for returning an `InputRequiredResult` from a tool handler is **UNVERIFIED** — the
  release notes confirm the feature, not the type names; confirm in the spike.
- `Microsoft.Extensions.Hosting` for a small EXE: the full generic host adds a few Microsoft.Extensions.* 
  assemblies (DI, configuration, options, logging abstractions) — a few MB at most next to a
  self-contained publish; acceptable for an EXE that lives in the app's already-large package.

### 3.3 Client support (verified, late 2026)

| Client | stdio | Config shape | MRTR elicitation |
|---|---|---|---|
| **Claude Code** | Yes — `claude mcp add <name> -- <command>`; requests protocol 2026-07-28 from local stdio servers since **2.1.292 (2026-10-06)** | CLI or config file | **Yes** — form mode since v2.1.76, URL mode since 2.1.281 (2026-09-23) on 2026-07-28 connections (2.1.287 for 2025-11-25); `Elicitation` hooks can auto-respond; non-interactive `claude -p` sessions ask the server to cancel `input_required` |
| **Cursor** | Yes — `~/.cursor/mcp.json` / `.cursor/mcp.json`, `mcpServers: { name: { command, args, env } }` | JSON | **UNVERIFIED** for MRTR specifically; note: Cursor's "MCP Network Controls" sandbox does not run on Windows — stdio servers matching a sandboxed Command entry won't start there |
| **VS Code (Copilot)** | Yes — portable `.mcp.json` / `~/.copilot/mcp-config.json` (`mcpServers`, `type: "stdio"` required); or `code --add-mcp '{"name":"larik","type":"stdio","command":"larik-mcp.exe"}'` | JSON | **UNVERIFIED** for MRTR; VS Code is mid-migration from `.vscode/mcp.json` (`servers` key) to the portable `mcpServers` format |
| **Codex CLI / IDE / ChatGPT desktop** | Yes — `codex mcp add larik -- larik-mcp.exe`; `[mcp_servers.larik] command = "larik-mcp.exe"` in `~/.codex/config.toml` | TOML (shared by all three surfaces) | Approval surfaced via Codex's own approval modes instead: `default_tools_approval_mode = "auto\|prompt\|writes\|approve"`, per-tool `tools.<name>.approval_mode` — i.e. an agent host that honors `readOnlyHint` natively |

The Tauri app's single `claude mcp add --transport http` snippet only serves Claude Code. A stdio
server gets all four clients with trivial per-client snippets (§5.6).

### 3.4 Packaging facts (verified)

- The app already runs full-trust inside MSIX (`runFullTrust` capability in
  `Larik/Package.appxmanifest`); a second EXE in the same package also runs with package identity,
  so it can resolve the package `LocalState` and read the same Credential Manager entries.
- **Execution alias**: a packaged console EXE is reachable from a terminal via a reparse point in
  `%LOCALAPPDATA%\Microsoft\WindowsApps` (on PATH). Verified manifest shape (from a real-world MSIX
  posted to the Cygwin list, 2025-10):

  ```xml
  <Application Id="Mcp" Executable="Larik.Mcp.exe"
      EntryPoint="Windows.FullTrustApplication"
      desktop4:Subsystem="console" desktop4:SupportsMultipleInstances="true">
    <uap:VisualElements .../>
    <Extensions>
      <uap5:Extension Category="windows.appExecutionAlias"
          Executable="Larik.Mcp.exe" EntryPoint="Windows.FullTrustApplication">
        <uap5:AppExecutionAlias desktop4:Subsystem="console">
          <uap5:ExecutionAlias Alias="larik-mcp.exe" />
        </uap5:AppExecutionAlias>
      </uap5:Extension>
    </Extensions>
  </Application>
  ```

  `desktop4:Subsystem="console"` keeps stdio attached; `SupportsMultipleInstances` lets several
  clients run the server concurrently (MSIX console apps are single-instance by default).
- **Known stdio caveat**: execution-alias launches broke stdio handle forwarding under
  Cygwin/MinTTY (fixed client-side in msys2-runtime 3.6.x, merged 2026-01-05); launching from
  cmd/PowerShell/Win32 `CreateProcess` parents works (Store Python and winget are the existence
  proofs). One VS Code *debugger*-specific report (cppvsdbg, Feb 2026) does not affect MCP clients.
  **Phase 0 must smoke-test each target client against the alias**; the portable build sidesteps
  this entirely with the real path.
- **Store build**: the workflow generates `obj\store\Package.appxmanifest` from Partner Center
  values — **UNVERIFIED** whether it preserves a second `<Application>` entry with the alias
  extension; check in Phase 0. Fallback: declare the alias on the existing App entry.
- **Loopback HTTP inside a packaged app** (for completeness): full-trust packaged apps bind
  loopback without AppContainer/firewall friction (Tauri already proves this pattern); with the
  stdio design we simply don't need a port at all.

### 3.5 Shared SQLite, secrets, and the provider seam (verified against this repo)

- `StudioStore` opens every connection with `PRAGMA journal_mode = WAL; busy_timeout = 5000`
  (`Larik/Services/StudioStore.cs` `Open()`). WAL permits many concurrent readers and one writer;
  the app's `Save()` transactions are short. Two processes (app + EXE) are therefore safe, with
  busy-time retries for the rare writer collision. **Migration races are benign by construction**
  (`CREATE TABLE IF NOT EXISTS` + `EnsureColumn` + idempotent `user_version` write), though the
  deferred transaction should be hardened to `BEGIN IMMEDIATE` when we add schema v7.
- **Audit writes from the EXE** share the same file: keep one store (single source of truth for the
  in-app audit UI); the EXE's audit INSERT retries on busy up to a small budget so an audit row is
  never silently lost.
- **`SecretStore`** uses `CredRead`/`CredWrite` (generic credentials, target
  `Larik/Connection/{spaceId}/{key}`), which is **per-user**: the EXE running as the same user reads
  the same entries with **no prompt**. No extra plumbing needed.
- **`LarikPaths`** resolves packaged state via `Windows.Storage.ApplicationData.Current.LocalFolder`
  and unpackaged via `%LOCALAPPDATA%\Larik WinUI`, with a `LARIK_STATE_DIR` override (used by the UI
  suite). A console EXE without the Windows App SDK can resolve the same path via kernel32
  `GetCurrentPackageFamilyName` + `%LOCALAPPDATA%\Packages\<PFN>\LocalState` — **UNVERIFIED until a
  5-minute spike** confirms the paths match; fallback is referencing the WinRT projections
  (`Microsoft.Windows.SDK.NET`).
- **Provider contract today**: row-capped execution ✓ (`ExecuteAsync(sql, db, QueryExecutionOptions)`),
  databases with access flags ✓ (`GetDatabaseDetailsAsync`), full schema ✓ (`GetSchemaAsync` +
  client-side table filter, like Tauri), routines ✓, row estimates ✓, provider-owned statement
  splitting ✓ (`IEditorLanguage.Split`/`SplitBatches`). **Missing: read-only execution.** §7 adds it
  additively.
- `DatabaseProviderCatalog` lives in dependency-free `Larik.Data`, so the EXE discovers providers
  exactly like `App.xaml.cs` does — no WinUI needed.

---

## 4. Architecture options

| Criterion | A. In-process HTTP in the WinUI app (Tauri parity) | B. stdio EXE only | **C. stdio EXE + named-pipe to app (recommended)** | D1. EXE speaking HTTP instead of stdio |
|---|---|---|---|---|
| App weight | Kestrel + ASP.NET Core inside the GUI process; listener thread; token UI | Zero when idle; EXE runs only while a client is connected | Same as B + one pipe-listen thread (negligible) | Same process weight as B, plus port + token management |
| Package size | + ASP.NET Core (~5 MB) into the app | + MCP SDK (~few MB); drivers already shipped | Same as B | Same as B |
| Works with app closed | **No** — the server dies with the app | **Yes** for reads; writes degrade (see §5.3) | **Yes** for reads; writes via elicitation or refused | Yes, but no better than B |
| Security surface | Loopback port + bearer token stored in each client's config (copyable) | No port, no token; OS process boundary = OS user | Same as B; pipe ACL'd to current user | Same as A |
| Approval UX | In-app dialog + toast (app is always up) | Client-side MRTR elicitation only — unusable with clients that don't support it | **Both**: in-app dialog + toast when running (always works), MRTR elicitation when closed | In-app only when app running; nothing when closed |
| Onboarding | One URL + token per client; token rotatable | One command per client, no secret | Same as B | URL + token |
| Store/MSIX constraints | None extra | Execution alias + multi-instance (known-good pattern, needs smoke test) | Same as B | None extra |
| Dev effort | Port 365 LOC server + token mgmt UI; SDK AspNetCore in a desktop app (threading/lifetime care) | SDK does the wire; tools + policy + audit | B + pipe protocol (~150 LOC both sides) + app dialog | B + HTTP/token UI |
| Testability | UI suite must speak raw HTTP | PowerShell can drive real stdio pipes end-to-end | Same as B + pipe tests are in-proc unit-testable | Raw HTTP harness again |

**Recommendation: C** — and it is really "B plus a progressive enhancement". The decisive rows are
*works with app closed* and *security surface*: Tauri's own design notes say the HTTP-in-GUI choice
was forced by rejecting a headless sibling; a .NET sibling is cheap here (the runtime, SqlClient,
Npgsql and Sqlite are already inside the package), the official SDK removes the hand-rolled
JSON-RPC risk entirely, and stdio deletes the port+token attack surface along with three user
decisions. The named pipe exists only for UX (approval dialog where the model can't click, toast,
open-as-tab); if it is absent, reads still work and writes degrade safely.

Rejected variants: hosting HTTP in the app via `ModelContextProtocol.AspNetCore` (adds Kestrel to a
desktop app for no headless benefit); a Windows service (overkill, harder approvals); "app embedded
in the EXE" (circular); MCP Apps extension UIs (requires extension negotiation; our dialog is
better and always available).

---

## 5. Recommended design

### 5.1 Projects and what moves

```
Larik.slnx
├── Larik.Data/            (unchanged rules: net10.0, zero packages)  + §7 additions
├── Larik.Data.SqlServer/  + T-SQL read-only policy (classifier) + read-only execution
├── Larik.Data.Postgres/   + BEGIN TRANSACTION READ ONLY wrapper + read gate
├── Larik.Core/            NEW, net10.0, no WinUI — the shared library:
│     LarikPaths, StudioStore (+ schema v7: mcp columns/tables), SecretStore, AppLog,
│     McpPolicy (access parse + per-space/global lookup), McpAuditStore, McpApprovalQueue (persisted),
│     ConnectionResolver (space → provider + profile + secrets), PipeProtocol (message shapes)
├── Larik/                 (WinUI app; references Larik.Core instead of the moved files)
│     + pipe server, approval dialog + toast, Settings→MCP page, workspace tri-state, audit list,
│       "open as tab" action
├── Larik.Mcp/             NEW console EXE, net10.0 (windows), no WinUI — the MCP server:
│     ModelContextProtocol 2.4.0, stdio transport, the tools, row/time/size caps,
│     per-space sessions, audit writes, pipe client, elicitation fallback
└── Larik.Data.Tests/      + classifier matrix, policy/store/pipe tests
```

Moving `StudioStore`/`SecretStore`/`LarikPaths`/`AppLog` from `Larik/Services` into `Larik.Core`
is mechanical (namespace-only for callers); behaviour must not change, verified by the UI suite
prefix. The app never references `Larik.Mcp`; the EXE never references `Larik` (WinUI).

### 5.2 Process model and lifetimes

| Situation | Behaviour |
|---|---|
| App not running, client spawns `larik-mcp` | Server starts in ~1–2 s (SDK host), reads the store, serves `list_connections`/`list_databases`/`get_schema`/`run_query` for spaces with `mcp_access != off` and `mcp_enabled = true`. Writes: MRTR elicitation if the client declares it; otherwise refused with an actionable message ("start Larik, or use a client that supports approvals") |
| App running | Same, plus: EXE connects to the pipe `larik.studio.mcp.v1` (current-user ACL) for approval dialogs + toasts; `open_in_studio` works |
| Client closes stdin | EXE exits promptly (stdio shutdown rule); sessions disposed, transactions rolled back |
| Two clients at once | Two EXE instances (multi-instance alias); each owns its own provider sessions; audit writes serialized by SQLite WAL |
| App starts while EXE runs | EXE reconnects to the pipe lazily on next approval/open request |
| App closes while EXE runs | Pipe write fails → approval in flight times out → auto-deny (Tauri's 2-minute rule); audit row `denied (app closed)` |
| Packaged single-instance rule | Untouched: the second-instance refusal belongs to `Larik.exe`; `Larik.Mcp.exe` is a separate identity-free entry point (its own `<Application>` Id) |

MCP-side **session lifecycle**: the EXE creates **one `IDatabaseConnection` per tool call** (via
`IDatabaseProvider.CreateConnection` + `ConnectionResolver`), never reusing a user tab session or
the space control session (AGENTS.md rule 9 honored by construction — the EXE has no access to
them at all). SqlClient/Npgsql internal pooling makes per-call cheap; a per-space semaphore caps
concurrency (2); every session is disposed in `finally` with rollback-on-close semantics like
`QuerySession`. Statement timeout 120 s via a CTS linked to the SDK's cancellation (stdio
`notifications/cancelled` maps to the provider's `CancellationToken`).

### 5.3 Policy model (single source of truth)

- `mcp_enabled` (`app_state`) — global switch, **default off**. The EXE re-reads it per call; off
  means every tool answers with "MCP is off in Larik Settings" (no list leakage).
- `spaces.mcp_access` — tri-state `off|read|write`, **default off**, schema v7 via `EnsureColumn`
  (no CHECK constraint; `McpPolicy.Parse` is fail-closed like Tauri's `Access::parse`, unit-tested).
- Read spaces: the EXE splits the query with the provider's own `IEditorLanguage.Split` (never a
  global splitter — hard rule 4), gates each statement through the provider-declared
  read-only policy (§7), and executes with `QueryExecutionOptions { ReadOnly = true, MaxRows = clamped }`.
- Write spaces: human approval first (below), then normal execution with the login's own permissions.
- Denials are messages an agent can act on, Tauri-style: quote the offending unit (300 chars), state
  the access level, and the two remedies (write access, or a read-only login).

### 5.4 Approvals

1. **Client declares elicitation (MRTR) and the app is not running** → the tool returns
   `InputRequiredResult` with a one-field form (`approve: boolean`, default false, message = space +
   statement preview + "runs with the saved login's permissions"). Claude Code renders it inline
   (verified); decision + latency recorded in the audit with `surface = client`.
2. **App running** (any client) → the EXE parks the request in the shared, persisted
   `McpApprovalQueue` (Tauri's `mcp_approvals` shape: pending → approved/denied/timeout/orphaned),
   sends it over the pipe; the app shows the existing-style in-app dialog + a Windows toast + taskbar
   flash. 2-minute auto-deny. Decision recorded with `surface = app`. The dialog is the one a
   compromised client cannot click — this is the tier that beats client-side approval.
3. **Neither** → refuse with the actionable message above. Fail closed everywhere.

Statement review cap 10,000 chars (refuse, don't truncate — Tauri's rule), full statement shown in
the dialog, 140-char preview in the toast.

### 5.5 Audit

Same tables Tauri proved, ported 1:1 into `StudioStore` schema v7 (`provider_id` instead of
`driver` to match this repo's naming): `mcp_log` (every call incl. denials, 10 k statement cap,
`decision_latency_ms`/`decided_at`/`decision_surface`) and `mcp_approvals` (lifecycle). The app's
Settings → MCP page renders a quiet read-only list (reuse the Archive page patterns); each row has
**Open as tab** (pipe → app creates a tab in that workspace with the statement preloaded) — the
"continue the agent's work in the studio" affordance. Both processes write through the same
`McpAuditStore` in `Larik.Core`; the app is the only reader.

### 5.6 Tool surface (5 tools, annotated)

| Tool | Annotations (`readOnlyHint`/`destructiveHint`/`idempotentHint`/`openWorldHint`) | Notes vs Tauri |
|---|---|---|
| `list_connections` | true/–/true/false | id, name, provider name, `DescribeEndpoint`, identity, access level. **Never** profile values or secrets; Mongo URI lives in Credential Manager here, and a `redact_host` port guards anyway |
| `list_databases` | true/–/true/false | `GetDatabaseDetailsAsync` (accessible-first, Tauri parity) |
| `get_schema` | true/–/true/false | `database` required, optional `table` filter (client-side, like Tauri's `fetch_table_columns` narrowing) and `include_routines`. Returns **column types** (`ColumnDetails`), PK, FK with `ReferencedSchema`, and **routine signatures with ordered params** — strictly richer than Tauri's cached tables/relationships |
| `run_query` | false/true/false/false | row_limit 200 default/1000 max, 120 s timeout, ~1 MiB response guard, per-result-set `truncated`/`row_limit`, `messages` (PRINT/RAISERROR — new), `generated_query` (Mongo parity), split via the provider language, classifier + `ReadOnly` enforcement on read spaces, approvals on write spaces |
| `open_in_studio` (new) | true/–/true/false | `connection`, `query` → pipe → app opens a tab. Error with a clear remedy when the app is closed |

`serverInfo.instructions` (first 512 chars self-contained — Codex documents that it reads this)
tells agents the workflow: list connections → schema → query; access levels; truncation meaning.

Deferred deliberately (UX lens: defer/remove): MCP resources (schema browsing — Claude Code is
tools-first; revisit for VS Code's resource browser), prompts, the Tasks extension (long-running
DML), MCP Apps, Skills.

### 5.7 Onboarding snippets (rendered with real paths in Settings → MCP)

```
Claude Code : claude mcp add larik -- larik-mcp.exe          (scope flag per current docs)
Cursor      : ~/.cursor/mcp.json  {"mcpServers":{"larik":{"command":"larik-mcp.exe"}}}
VS Code     : code --add-mcp '{"name":"larik","type":"stdio","command":"larik-mcp.exe"}'
Codex       : codex mcp add larik -- larik-mcp.exe
```

Packaged builds resolve the alias; the portable build substitutes the installed EXE path. One
segmented control picks the client; one Copy button. No token, no port, no URL.

---

## 6. Better than Tauri — concretely

1. **Headless reads.** The server lives outside the GUI; agents work when Larik is closed. Evidence:
   Tauri MCP_DESIGN.md rejected exactly this ("headless stdio sibling rejected for v1"); Claude
   Code has required protocol 2026-07-28 over stdio since 2.1.292. Cost: the EXE project + packaging.
2. **No port, no token.** Three settings (port, token, regenerate) and their risks (probeable
   loopback port, token in world-readable client configs) are deleted; trust boundary becomes the
   OS user. Cost: none — it *removes* work.
3. **Official SDK vs 365 hand-rolled JSON-RPC LOC.** Versioning, discovery, MRTR, cancellation,
   restart semantics and legacy-client fallback are maintained upstream and conformance-tested by
   the ecosystem (52.5M downloads; Aspire/Semantic Kernel depend on it). Cost: a few MB of
   Microsoft.Extensions.* binaries already adjacent to what ships.
4. **Approvals appear where the user already is.** With the app closed and a capable client
   (Claude Code verified: form dialogs since v2.1.76, hooks to auto-respond), the approval renders
   in the agent's terminal instead of forcing a Larik focus switch. With the app running, the
   in-app dialog + toast is *stronger* than Tauri's (a compromised client cannot click it) — both
   tiers log their surface. Cost: MRTR handling + one fallback path.
5. **Stronger SQL Server read mode.** Tauri admits its MSSQL classifier is best-effort and runs
   statements on the login's full permissions. We add an **always-rollback transaction** around
   every read-mode execution (`SqlTransaction` + unconditional `Rollback`) on top of the same
   fail-closed classifier, plus `ApplicationIntent=ReadOnly` on the MCP-side connection string
   (harmless standalone, routes to readable secondaries on AG). Residuals stay and are documented
   (§8) — but misclassified *transactional* writes now roll back instead of committing. Cost:
   provider-local change in `Larik.Data.SqlServer`.
6. **Strong PostgreSQL read mode with zero session races.** `BEGIN TRANSACTION READ ONLY` + `ROLLBACK`
   per call gives engine-level rejection per statement without Tauri's startup-options coupling.
   Cost: provider-local.
7. **Provider-owned policy instead of a central classifier.** `mcp/policy.rs` branches on driver
   kind inside the server; our policy is declared by each provider (`ReadOnlyPolicy`), so new
   engines (MongoDB add-on) plug in without app or EXE changes, and old add-ons fail closed. This is
   hard-rule-1-compliant by construction. Cost: §7 contract additions.
8. **Tool annotations.** `readOnlyHint`/`destructiveHint` on every tool; Codex's
   `default_tools_approval_mode = "writes"` and Claude Code's pre-approval UX then treat our reads
   as prompt-free and writes as confirm-first without any server-side hacks. Tauri (pre-2025-06-18
   wire shape) has none. Cost: a few properties.
9. **Richer `get_schema`.** Typed columns, PK, FK with referenced schema, and routine signatures —
   agents write correct joins and EXEC calls sooner. Cost: none (the contract already has it).
10. **Per-statement messages and generated queries in results.** `PRINT`/`RAISERROR` output and
    Mongo's compiled pipeline arrive in tool results — debugging information Tauri's payload lacks.
11. **Four-client onboarding.** Copy-paste for Claude Code, Cursor, VS Code, Codex vs Tauri's single
    `claude mcp add` line. Cost: one segmented control.
12. **"Open as tab" continuation.** One click in the audit list (or one agent tool call) drops the
    exact statement into the right workspace as a query tab. Cost: pipe + small app plumbing.
13. **Honest, testable.** The UI suite can spawn the real EXE over real stdio pipes from PowerShell;
    Tauri needed raw-socket HTTP harness code inside the server module.

Where Tauri still wins (kept): the audit schema, limits (200/1000 rows, 120 s, 1 MiB, 2-min
approval, 10 k review cap) are adopted as-is; in-app approval + toast remain the strongest tier.

---

## 7. Contract / provider changes (additive only)

All changes follow hard rule 3: default implementations, nullable `init` properties, no renames, no
positional record parameters. Old plugins keep compiling and loading.

1. **`QueryExecutionOptions`** (Larik.Data) — add a nullable init property in the record body:

   ```csharp
   /// <summary>True asks the provider to enforce read-only semantics as deep as the engine
   /// allows. Null (default) is the historical read-write behaviour, so older callers and
   /// providers are unchanged.</summary>
   public bool? ReadOnly { get; init; }
   ```

2. **New optional capability interface** (Larik.Data, zero dependencies):

   ```csharp
   public interface IReadOnlyStatementPolicy
   {
       /// <summary>Fail-closed statement gate: true only when the statement is provably a read.
       /// Called per already-split statement unit before execution.</summary>
       bool IsProvenRead(string statement);
   }
   ```

   and on `IDatabaseProvider`, with a default so every existing provider (and the MongoDB add-on)
   keeps working:

   ```csharp
   /// <summary>How this engine proves a statement read-only at access level 'read'. Null means
   /// the host must refuse read-mode execution for this provider (fail closed).</summary>
   IReadOnlyStatementPolicy? ReadOnlyPolicy => null;
   ```

3. **Enforcement placement** (no provider-id branches anywhere):

   | Concern | Owner | Where |
   |---|---|---|
   | Global + per-space access, fail-closed parse | `Larik.Core` `McpPolicy` | shared store, used by EXE and app |
   | Statement gate for read spaces | provider's `IReadOnlyStatementPolicy` | `Larik.Data.SqlServer` (T-SQL classifier — port Tauri's `policy.rs` lists incl. `xp_`/`sp_`/`OPENROWSET`/`INTO`, with its test matrix), `Larik.Data.Postgres` (banned leads/anywhere lists) |
   | Engine-level read enforcement | provider's `ExecuteAsync` when `options.ReadOnly == true` | SQL Server: wrap each batch in a `SqlTransaction`, always `Rollback`, `ApplicationIntent=ReadOnly` on MCP-created connections; PostgreSQL: `BEGIN TRANSACTION READ ONLY` … `ROLLBACK` |
   | Splitting for the gate | provider's `IEditorLanguage.Split` | EXE calls it; no global splitter |
   | Row/time/size caps, audit, approvals | `Larik.Mcp` + `Larik.Core` | EXE-side |

   The MongoDB add-on can later ship its own policy (its shell parser already classifies); until it
   does, read-space `run_query` on MongoDB refuses with the standard message — fail closed, no app
   change, old add-on versions unaffected.

4. **`StudioStore` schema v7** (additive, `EnsureColumn`-style): `spaces.mcp_access TEXT NOT NULL
   DEFAULT 'off'`; `mcp_log` and `mcp_approvals` tables (Tauri's proven columns, `provider_id`
   naming); harden the migration transaction to `BEGIN IMMEDIATE` while touching it.

No other contract changes are needed: row caps, databases-with-access, schema, routines, row
estimates, transaction state and generated-query reporting all exist today.

---

## 8. Security analysis

**Assets**: saved credentials (Credential Manager), query results (data exfiltration), database
state (integrity), Larik's own store (audit integrity).

**Trust boundaries**: (a) MCP client ↔ EXE — the client is user-configured software, but *model
output is untrusted input*: query text from an agent is attacker-controlled for prompt-injection
purposes; (b) EXE ↔ SQLite/Credential Manager — same OS user (stdio's trust model; the MCP spec has
no authorization for stdio because the OS user *is* the boundary); (c) EXE ↔ app pipe — current-user
ACL only.

**What MCP adds vs the status quo**: no new privilege. Any process the user runs could already call
`CredRead` and use their databases; MCP adds *convenience*, gated by explicit opt-in. The HTTP+token
model (Tauri) is the variant that adds a stealable secret; stdio removes it.

**Read-only strength per engine (honest)**:

| Engine | Layers | Stops | Does not stop |
|---|---|---|---|
| SQL Server | fail-closed classifier (no EXEC/`xp_`/`sp_`/OPENROWSET/`INTO`/SET/USE/BEGIN/…, literals stripped first) + always-rollback transaction + `ApplicationIntent=ReadOnly` | every write/DDL verb it can lex; misclassified *transactional* writes (rolled back) | non-transactional side effects: some extended procedures, linked-server/remote writes reachable from misclassified reads, session-scoped temp objects (harmless — vanish with the per-call session). `ApplicationIntent` only routes on AG listeners; it is not enforcement. Honest label: best-effort+, stronger than Tauri by one layer; a **read-only login remains the only hard guarantee** (offered in UI copy, like Tauri) |
| PostgreSQL | `BEGIN TRANSACTION READ ONLY` per call (server rejects writes) + banned-leads/anywhere gate (no SET/BEGIN/COPY/DO/CALL/dblink/…) | all writes and DDL server-side; session-escape attempts | reads of sensitive data (by design — the user opted the space in) |
| MongoDB (add-on, later) | shell allow-list + `$out`/`$merge` denial (as Tauri ships) | parsed write methods | until the add-on adopts `IReadOnlyStatementPolicy`: read spaces refuse `run_query` entirely |

**What the human-in-the-loop buys**: the write approval stops a *rogue model* (it cannot click), and
the app-side dialog additionally stops a *compromised client* (it cannot click either — the
decision channel is the pipe to the app, not the MCP connection). Elicitation-tier approval stops
only the rogue model. A compromised client that can forge elicitation acceptances can make the EXE
run SQL — the same SQL it could have requested anyway; the saved login's permissions remain the
final boundary. Audit records which surface decided.

**Redaction**: `list_connections` returns provider display name, `DescribeEndpoint`, identity and
access level — never raw profile values; a port of Tauri's `redact_host` guards any URL-shaped
endpoint. Password-kind settings never leave `SecretStore`/the provider.

**When the app is closed**: reads work (state store + Credential Manager are user-scoped, no
prompt); writes need an elicitation-capable client, else refusal; orphaned approvals are swept on
next app start (Tauri's pattern).

**DoS/resource**: row caps (200/1000), 120 s statement timeout with real cancellation, ~1 MiB
response guard, 10 k approval/audit statement caps, per-space session semaphore (2), multi-instance
EXE noted per-process (global caps are future work).

---

## 9. Effort and phases

Sizes are honest working days for one developer who knows this codebase.

| Phase | Content | Days | Unlocks / tests |
|---|---:|---|---|
| **0. Spike** (decision gate) | Execution-alias + stdio round trip from a packaged build with Claude Code and one more client; shared-runtime publish layout (two apphosts, one runtime folder) or accept a self-contained EXE; Store-manifest survival of the alias; `GetCurrentPackageFamilyName` LocalState equality; C# SDK MRTR API shape | 1–2 | Kill/confirm the UNVERIFIED items; package-size measurement (MB, not vibes) |
| **1. Core** | `Larik.Core` extraction (store/secrets/paths/log — behaviour-identical); schema v7; `Larik.Mcp` EXE with SDK 2.4.0 stdio + the 4 parity tools; classifier + read-only execution in the two providers; audit writes; PowerShell end-to-end smoke | 4–6 | Reads end-to-end. `Larik.Data.Tests`: classifier matrix (port Tauri's tests), fail-closed access parse, row clamp, `ReadOnly` defaults, migration + two-process WAL opens, audit round-trip. UI suite: spawn-EXE check |
| **2. Approvals** | Pipe server in app + in-app dialog + toast + persisted queue + 2-min auto-deny; write mode live; MRTR elicitation fallback | 3–5 | Write path end-to-end. UI suite: approval dialog driven by a real EXE call, deny/approve/timeout audit rows |
| **3. UX + docs** | Settings → MCP page (global switch, audit list with Open-as-tab, per-client snippets), workspace tri-state with outcome labels, FEATURES/README/PROVIDERS updates, full UI suite run (ask first) | 3–4 | Feature complete |

Total ≈ **10–15 days** + spike. Test layering per the repo rules: everything provable in
`Larik.Data.Tests` stays there (classifier, policy parse, store, pipe protocol in-proc); the UI
suite gets ~5 checks that drive the real EXE over stdio (list tools, read query + audit row, denied
write on a read space, approved write via the dialog, global-switch-off refusal, tri-state
persistence). Deferred: resources/prompts (await demand), Tasks extension (long DML), read-only
login helper (a "Create read-only login" affordance in workspace settings for SQL Server —
provider-generated DDL; genuinely useful, needs its own design pass).

---

## 10. Open questions for the user

1. **Approval tier when the app is running**: in-app dialog only (recommended — strongest, no
   double prompt), or also elicit to the client so the user can answer without leaving the agent?
2. **Write mode with the app closed**: MRTR elicitation where supported (recommended), or refuse
   outright until the app is running ("writes need the studio open" — simpler story, fewer paths)?
3. **Store edition**: ship MCP in both editions, or GitHub-first and Store later? (No technical
   blocker found; only the manifest-generation check remains.)
4. **Default `row_limit`**: keep Tauri's 200/1000, or start lower for agent context budgets?
5. **`open_in_studio` as an agent tool**: keep (recommended), or make it app-side-only (audit-list
   button) to keep the tool surface at parity's four?

---

## Implementation status (2026-11-12, branch `feat/mcp-server`)

Phase 1 of §9 is in the tree. What exists now:

| Area | State |
|---|---|
| `Larik.Core` | Created; `StudioStore`, `SecretStore`, `LarikPaths`, `AppInfo`, `AppLog`, `AppTheme` and `VirtualReferenceRecord`/`IVirtualReferenceStore` moved in. Plain `net10.0`: package detection is a kernel32 `GetCurrentPackageFamilyName` call, not WinRT. Namespaces unchanged (`Larik.Services`, `Larik.Models`) to keep ~50 call sites untouched. |
| Schema v7 | `spaces.mcp_access` (default `off`), `mcp_log`, `mcp_approvals`; `StudioStore` is `partial`, MCP persistence lives in `McpStore.cs`. |
| `Larik.Mcp` | Console EXE, `ModelContextProtocol` 2.4.0 + `Microsoft.Extensions.Hosting`, stdio transport, four tools (`list_connections`, `list_databases`, `get_schema`, `run_query`), logs to stderr only. `--state <dir>` points it at a packaged studio's store when it is started without package identity. |
| Settings → AI agents | New Settings section: global switch, per-workspace access rows, per-client onboarding command with copy, activity list with clear (refreshed while visible). |
| Approvals | `Larik/Services/McpApprovalService.cs` owns a current-user pipe (`larik.studio.mcp.v1`), the dialog (Deny is the default button), a Windows notification plus taskbar flash when the window is not in front, and the two-minute auto-deny. `Larik.Mcp/ApprovalChannel.cs` parks the call and waits. `mcp_approvals` records the lifecycle; the audit row carries `decision_surface = app`. |
| Limits | 120 s statement cap (verified against LocalDB: a `WAITFOR` past the cap returns "stopped after N seconds" rather than the engine's cancellation text), 200/1000 rows, and a 1 MiB payload budget that drops rows (`McpResponseBudget`, unit-tested) instead of discarding the result. Two calls per workspace run at once. |

Deviation inside the approval work: **the toast needs packaging**. `AppNotificationManager.Register()`
requires the package to declare the App Notification COM activator; the debug identity registers none,
so the app logs `notifications unavailable` and falls back to the dialog plus a taskbar flash. The
manifest entry belongs with the execution alias in the packaging step.
| Contracts | `QueryExecutionOptions.ReadOnly` (nullable), `IReadOnlyStatementPolicy`, `IDatabaseProvider.ReadOnlyPolicy` (default null). |
| Enforcement | `Larik.Data/ReadOnlySql.cs` scanner + `TSqlReadOnlyPolicy` / `PostgresReadOnlyPolicy` / `DemoReadOnlyPolicy`; SQL Server runs read-mode batches in an always-rolled-back transaction; PostgreSQL in `BEGIN TRANSACTION READ ONLY`. |
| Tests | `Larik.Data.Tests/ReadOnlyStatementTests.cs` (allow/refuse matrix, policy presence, opt-in default) and `McpPipeTests` (wire round trips, fail-closed access parsing). 218 tests green. |
| UI suite | `ui-tests.ps1`: **94/94 checks pass (≈16 min)**, including *settings can expose workspaces to AI agents* and *an agent write waits for approval in the studio* (spawns the real EXE, approves, asserts the audit row). |
| Packaging | `CopyMcpServer` + `PublishMcpServer` put `Larik.Mcp.exe` beside the app (publish verified: the self-contained publish folder contains it); a second `Application` with an `uap5:ExecutionAlias` for `larik-mcp.exe` is declared, and the alias launches once the EXE is in the package layout. |
| Notifications | The approval toast works in a packaged build: `Register()` is the unpackaged path and now runs only there, so `Show()` reaches the Windows 11 notification centre (verified: "Larik SQL Studio · An agent wants to run a statement"). |
| UI | Settings → AI agents (switch, per-workspace Off/Read only, per-client command, activity list). Covered by `ui-tests.ps1` check *settings can expose workspaces to AI agents*. |
| Smoke | Real EXE over stdio against a temp state folder: off-by-default refusal, list/schema/query on a seeded demo workspace, write refusal, mixed-batch refusal, audit rows. Repeated against a live SQL Server 2025 workspace (read query returned rows, `create table` refused, audit rows appeared in Settings). Approval round trip: the dialog blocked the agent, **Approve** ran the statement, **Deny** returned an actionable error, and an unanswered prompt auto-denied at exactly two minutes (`the approval prompt timed out; denying` in the log). |

Deviations from this document, and why:

1. **`ApplicationIntent=ReadOnly` is not set.** The ADO.NET connection string is built once in
   `OpenAsync`, while `ReadOnly` is per execution; threading it through would add a connection-level
   concept for a flag that only routes on Availability Group listeners. The rollback transaction is
   the real second layer. Revisit if AG routing ever matters.
2. **Approvals are in-app only.** The dialog, the notification, the taskbar flash and the
   two-minute auto-deny are implemented, and the agent's call blocks throughout. Client-side
   elicitation (MRTR) is **not** implemented: with the studio closed a write is refused and audited
   as `orphaned`. The SDK seam is ready (`McpServer.IsMrtrSupported`, `InputRequiredException`,
   `Params.InputResponses`), but the round trip needs a live capable client to validate, so shipping
   it blind would risk a retry loop.
3. **No MSIX execution alias or packaging yet**, so `larik-mcp` is not on PATH and clients need the
   EXE path. That work also settles the shared-runtime publish layout the spike would have measured.
4. **The Demo provider got a policy** (`DemoReadOnlyPolicy`) so the smoke test and the UI suite can
   exercise read mode without a server.
5. **Settings → AI agents shipped with this phase** instead of Phase 3, because the feature cannot be
   turned on without it. It offers Off and Read only: a Write option that refuses every call would be
   a switch that does nothing.
6. **`--state <dir>`** was added to the server. A packaged studio's store lives inside the package,
   and an EXE started by path has no package identity to find it; the flag removes the guess until
   the execution alias makes it unnecessary.

Two bugs the UI suite found in this change, both fixed:

- The activity list stopped refreshing after the Settings page was left and re-entered: the page is
  cached, but its refresh timer was only started from the nav handler, so a cached instance came back
  without a live timer. It now stops on `Unloaded` and starts on `Loaded`.
- The approval service opened the store synchronously during `OnLaunched`, putting a second SQLite
  connection on the path to the first window. It is fire-and-forget now.

---

### Sources (fetched 2026-11-12)

- MCP specification: modelcontextprotocol.io — `/specification/latest`, `/specification/2026-07-28/basic/transports`,
  `/basic/transports/stdio`, `/basic/versioning`, `/client/elicitation`, `/server/tools`; ToolAnnotations
  schema (2026-07-28 + 2025-11-25), tool-annotations blog (2026-03-16)
- C# SDK: github.com/modelcontextprotocol/csharp-sdk (README, v2.0.0 release notes);
  nuget.org/packages/ModelContextProtocol (2.4.0, dependencies, version history);
  csharp.sdk.modelcontextprotocol.io/v1/concepts/getting-started.html
- Clients: code.claude.com/docs/en/mcp (+ claude-code issue #88075 timeline for elicitation);
  cursor.com/docs/mcp; code.visualstudio.com/docs/agent-customization/mcp-servers and
  /docs/agents/reference/mcp-configuration; Codex docs + openai/codex `mcp_types.rs`
- Packaging: Cygwin list post "Windows App execution alias doesn't forward stdin properly"
  (2025-10, manifest shape + MinTTY issue), msys2-runtime PR #322 (fix, merged 2026-01-05),
  vscode-cpptools issue #14216 (debugger-only), CPython `test_appexeclink` (reparse-point alias facts)
- SQLite WAL/busy handling: sqlite.org/wal.html, sqlite.org/c3ref/busy_handler.html (standard
  reference docs; facts as established there)
- Repos: `C:\dev\larik-sql-studio` (MCP_DESIGN.md, `src-tauri/src/mcp/*`, `storage/mcp_log.rs`,
  `storage/database.rs` migrations, `commands.rs`, frontend MCP surfaces) and
  `C:\dev\larik-winui` (AGENTS.md, docs/*, Contracts.cs, StudioStore.cs, SecretStore.cs,
  QuerySession.cs, SpaceViewModel.cs, LarikPaths.cs, csproj files, Package.appxmanifest)
