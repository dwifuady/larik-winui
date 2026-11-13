# Add-ons: provider distribution design

Status: the contract package, add-on loader, index client, runtime install without a restart,
install/remove, the Drivers page and the Store-edition gate are implemented. MongoDB ships only as
an add-on. Not yet: a signed index and arm64 index entries. This is the companion to
[`PROVIDERS.md`](PROVIDERS.md), which covers the provider contract. This file covers how a provider
assembly leaves the app package, how it is discovered, verified, installed, loaded and distributed
through the Store and winget.

The short version: a curated JSON index in a GitHub repository, provider zips in their own
repositories, SHA-256 pinned in the index, install into `%LOCALAPPDATA%`, load in a per-add-on
`AssemblyLoadContext`, restart to apply. No backend, no database.

---

## 1. Decision summary

### Terminology

User-facing copy says **driver** (the DBeaver/DataGrip word for the same thing). The index, zip and
folder names still say **add-on** (`larik-addon.json`, `Plugins`, `addons` in the index), and the
contract says **provider** (`IDatabaseProvider`). They are three names for the same object at
different layers: driver for the person connecting, add-on for the package, provider for the code.

### Store edition

`-c Store` compiles `LARIK_STORE`. `AddonPolicy.AddonsEnabled` is false, so `DriverRuntime` scans
only the built-ins, Settings → Drivers and the install entry point are hidden, and a missing driver
reads “Not included in this edition”. The Store build never downloads or loads a driver.
`official.json` is reserved for a future signed first-party channel; the Store package is
built-ins only today. See [`STORE.md`](STORE.md).

| Decision | Choice | Why |
|---|---|---|
| Catalogue hosting | Static `index.json` in a repo we own | No server to run, reviewable by PR, works offline with "Install from zip" |
| Approval | Pull request + CI + human review | Curated index, not an open marketplace |
| Artifact source | The author's own GitHub Release zip | Providers stay in their owners' repos |
| Integrity | SHA-256 pinned in the index; index signing is planned, not shipped | A hash detects a swapped zip but not a compromised index |
| Install location | `%LOCALAPPDATA%\Larik WinUI\Plugins\<id>\<version>\` | MSIX install folder is read-only; portable and Store builds both work |
| Loading | One non-collectible `AssemblyLoadContext` per add-on | Dependency isolation; `Larik.Data` binds to the host |
| Apply | Restart required | .NET cannot unload assemblies truthfully |
| Channels | `official` (Store) and `all` (portable/winget) | Store certification posture differs from sideload |
| First-party engines | SQL Server + Demo in core; PostgreSQL bundled; MongoDB the first add-on | See §13 |

**Non-goals for v1:** sandboxing (add-ons run in-process with full trust), paid add-ons,
automatic background updates, unload-without-restart, a general-purpose plugin API beyond
`IDatabaseProvider`, and any provider contract change that is not additive.

---

## 2. What ships in the core app

The core keeps only what the product promises out of the box:

- **Demo** — onboarding, offline use, UI tests.
- **SQL Server** — the default provider (`DatabaseProviderCatalog.Default`). Removing it would
  change first-run behaviour and the connection dialog's default profile.
- **PostgreSQL** — bundled. It is a headline engine, only ~2 MB (Npgsql is a single managed
  assembly), and the Store build has no installer components, so bundling is the simplest
  no-dynamic-code path. See §13 for the reasoning.

Everything else — MongoDB today, MySQL/SQLite later — is an add-on. The assembly shape is the
same either way; a bundled provider must still be loadable from a folder so it can move out
later without rework.

> Rule: **the shell never knows a provider.** Bundled vs add-on is a composition-root detail,
> not a branch in the editor, rail or view models.

---

## 3. On-disk layout

```
%LOCALAPPDATA%\Larik WinUI\            (or LARIK_STATE_DIR)
├─ Larik WinUI\...                     (existing store state)
├─ Addons\
│  ├─ index.json                       (cached catalogue + ETag sidecar)
│  └─ index.etag
└─ Plugins\
   ├─ larik.mongodb\
   │  ├─ current.json                  { "version": "1.0.0" }
   │  ├─ 1.0.0\
   │  │  ├─ larik-addon.json
   │  │  ├─ Larik.Data.MongoDB.dll
   │  │  ├─ MongoDB.Driver.dll
   │  │  ├─ ...
   │  │  ├─ LICENSE
   │  │  └─ THIRD-PARTY-NOTICES.md
   │  └─ 1.1.0\                        (kept for rollback until the next successful start)
   └─ larik.mysql\ ...
```

The current version is a **pointer file**, not a symlink (symlinks need elevation on some
machines). Reading `current.json` is the only way the loader resolves an add-on, which makes
rollback a file write.

Installed state lives on disk, not in `StudioStore`, so a manual copy of the `Plugins` folder
works and an add-on survives a state-file reset. The cached index is disposable.

A packaged (MSIX) run keeps its state in the package's `LocalState`, so the Store build has its own
`Plugins` folder, separate from the portable and installer builds. The Store build never scans it
(add-ons are disabled there); the note only matters if a signed first-party channel ever uses it.
That is acceptable, but it means:

- add-on installation is per channel, not shared;
- uninstalling the Store package removes its add-ons;
- the UI must never print a raw path that looks identical across channels.

`LarikPaths.PluginsDirectory` already resolves through
`Environment.GetFolderPath(LocalApplicationData)`; keep it as the single source of truth.

---

## 4. The index

### 4.1 URL and caching

- Primary: `https://raw.githubusercontent.com/dwifuady/larik-addons/main/index.json` for the open
  channel and `.../official.json` for the Store channel.
- Fallback: GitHub Pages mirror of the same file.
- Override for tests and self-hosted mirrors: `LARIK_ADDON_INDEX`.
- Fetch with `If-None-Match`; cache the body and ETag in `Addons\`. Refresh at most once per
  day, and on an explicit "Check for updates" action.
- Never call the GitHub API from the app. `raw` has no token, no rate-limit surprises, and no
  `latest` redirects.

### 4.2 Shape

```json
{
  "schemaVersion": 1,
  "channel": "all",
  "generatedAt": "2026-11-10T00:00:00Z",
  "addons": [
    {
      "id": "larik.mongodb",
      "name": "MongoDB",
      "publisher": "Larik",
      "summary": "MongoDB provider with a T-SQL surface and mongosh passthrough.",
      "version": "1.0.0",
      "contract": ">=1.0 <2.0",
      "minAppVersion": "0.4.0",
      "platforms": ["win-x64", "win-arm64"],
      "providerIds": ["mongodb"],
      "url": "https://github.com/larik-sql/larik-mongodb/releases/download/v1.0.0/larik.mongodb-1.0.0-win-x64.zip",
      "sha256": "9f2c...",
      "sizeBytes": 10485760,
      "license": "MIT",
      "homepage": "https://github.com/larik-sql/larik-mongodb",
      "signature": { "keyId": "larik-index-2026", "value": "base64..." }
    }
  ]
}
```

| Field | Required | Meaning |
|---|---|---|
| `schemaVersion` | yes | Index format version. The app rejects an unknown major. |
| `channel` | yes | `official` or `all`. A build only accepts an index whose channel it trusts. |
| `generatedAt` | yes | Informational; never used for ordering. |
| `id` | yes | Stable add-on id, reverse-DNS. Never reused by a different publisher. |
| `version` | yes | SemVer of the add-on. |
| `contract` | yes | Range over `Larik.Data`'s contract version, `>=x.y <z.0`. |
| `minAppVersion` | yes | Oldest app that can load it. |
| `platforms` | yes | `win-x64`, `win-arm64`. `win-x86` only if the app ships it. |
| `providerIds` | yes | The `IDatabaseProvider.Id` values this add-on answers. Used for duplicate detection and for the "add-on not installed" message. |
| `url` | yes | Immutable release asset. No `latest/download`, no mutable branch. |
| `sha256` | yes | Lowercase hex of the exact zip bytes. |
| `sizeBytes` | no | Download size for the UI. Not trusted for enforcement. |
| `license`, `homepage` | yes | Shown in the add-on page; licence must match the zip. |
| `signature` | yes (v1) | Detached signature over the canonical entry. |

**Immutability rule:** an entry is never edited in place. A new version is a new entry keyed by
`id + version`; the index lists one current version per platform and keeps `previous` for
rollback only if the app needs it.

### 4.3 Why the index is signed

Pinning `sha256` protects against a corrupted or swapped download. It does **not** protect
against a compromised index repo: an attacker who can edit `index.json` can also point the hash
at their own zip. The app therefore verifies a detached signature over the canonical entry with
a public key compiled into the binary. Rotate keys explicitly; keep the old key id in the app
for one release.

v1 may ship hash-only if signing lands in the same release. Shipping a live, unsigned index is
not acceptable once third parties are allowed.

---

## 5. The package

A provider repo publishes one zip per platform:

```
larik.mongodb-1.0.0-win-x64.zip
├─ larik-addon.json
├─ Larik.Data.MongoDB.dll
├─ MongoDB.Driver.dll
├─ MongoDB.Bson.dll
├─ ...
├─ LICENSE
└─ THIRD-PARTY-NOTICES.md
```

```json
{
  "manifestVersion": 1,
  "id": "larik.mongodb",
  "version": "1.0.0",
  "publisher": "Larik",
  "contract": ">=1.0 <2.0",
  "providerIds": ["mongodb"],
  "entryAssembly": "Larik.Data.MongoDB.dll",
  "platforms": ["win-x64", "win-arm64"],
  "license": "MIT"
}
```

Rules:

- The index entry is authoritative; the manifest exists so an **offline zip install** can show
  metadata and so CI can cross-check the two.
- `entryAssembly` must implement `IDatabaseProvider` through the host's `Larik.Data`.
- **No install scripts, no `dotnet` runtime download, no post-install hooks.** The zip is
  extracted and read; it never executes anything during install.
- The provider project must publish with `GenerateDependencyFile=true` so
  `AssemblyDependencyResolver` has a `.deps.json`. `PublishSingleFile` and `PublishTrimmed` stay
  off.
- Each zip carries its own licence notices. `THIRD-PARTY-NOTICES.md` in the app is not a
  substitute for a driver that ships separately.

---

## 6. Approval

The index repo is the gate. A submission is a pull request that adds one entry.

CI on the index PR:

1. Validates the JSON against `schema.json` and the signature.
2. Downloads the `url` and recomputes `sha256`; fails on mismatch or a mutable URL.
3. Unzips in a throwaway directory and validates `larik-addon.json` against the index entry
   (`id`, `version`, `contract`, `providerIds`, `platforms`).
4. Loads `entryAssembly` in a probe process and asserts every `providerIds` value resolves to a
   public, non-abstract `IDatabaseProvider` with a parameterless constructor.
5. Rejects a `providerIds` value already claimed by another add-on.
6. Checks `LICENSE` and `THIRD-PARTY-NOTICES.md` exist.
7. Optionally scans the assembly references for banned APIs (`Process.Start`,
   `Assembly.LoadFrom` on downloaded paths, registry writes, network to non-package hosts).

Human review checks what CI cannot:

- Does it do anything beyond implementing `IDatabaseProvider`?
- Does the provider accept a connection string and credentials only through `ProviderSetting`?
- Does it degrade gracefully on a least-privileged login, and honour `EffectiveMaxRows`?
- Is the licence compatible, and are notices complete?
- Is the publisher identifiable and the source repository public?

**Revocation:** delete the entry and merge. Already-installed copies keep working; the app shows
"no longer in the index" on the add-on page and offers removal. There is no remote kill switch
for code already loaded, and the design should not pretend otherwise.

---

## 7. Loading model

### 7.1 Context per add-on

```csharp
internal sealed class AddonLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly Assembly _contract = typeof(IDatabaseProvider).Assembly;

    public AddonLoadContext(string directory, string entryAssembly)
        : base($"larik-addon:{Path.GetFileName(directory)}", isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(Path.Combine(directory, entryAssembly));
    }

    protected override Assembly? Load(AssemblyName name)
    {
        // The contract must be the host's instance, or the plugin's IDatabaseProvider is a
        // different type and discovery finds nothing.
        if (string.Equals(name.Name, _contract.GetName().Name, StringComparison.OrdinalIgnoreCase))
        {
            return _contract;
        }

        // Share anything the host already loaded (System.*, Microsoft.*, CommunityToolkit.*).
        // Otherwise resolve from the add-on folder, so Npgsql/Mongo versions stay isolated.
        var shared = Default.Assemblies.FirstOrDefault(
            a => string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase));
        if (shared is not null)
        {
            return shared;
        }

        var path = _resolver.ResolveAssemblyToPath(name);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string name)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(name);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
```

Notes:

- `isCollectible: false`. Updates are versioned folders + restart, not unload. Collectible
  contexts with leaked types are a debugging trap.
- Sharing already-loaded host assemblies keeps one `System.Text.Json`, one logging stack and one
  WinRT surface; per-add-on copies of framework assemblies cause the usual
  `InvalidCastException`/`MissingMethodException` confusion.
- Native dependencies (libmongocrypt, SNI) resolve through `ResolveUnmanagedDllToPath` when the
  `.deps.json` lists them.

### 7.2 Discovery

`DatabaseProviderCatalog.Initialize` currently takes `IEnumerable<Assembly>` for built-ins and
walks plugin directories itself with `Assembly.LoadFrom` (default context). Change it to:

```csharp
public static void Initialize(
    IEnumerable<Assembly> builtInAssemblies,
    IEnumerable<ProviderSource> addOnSources);

public sealed record ProviderSource(string DisplayName, Assembly Assembly);
```

The composition root builds `AddonLoadContext`s and passes loaded assemblies. `LoadErrors` gains
an origin (`"larik.mysql 1.2.0: ..."`) so the connection dialog can name the add-on instead of a
bare file.

### 7.3 Contract versioning

- Keep `Larik.Data`'s **`AssemblyVersion` pinned at `1.0.0.0`**. Plugin binding is by identity;
  bumping `AssemblyVersion` breaks every existing plugin. The real version lives in
  `AssemblyInformationalVersion`.
- Add a stable constant:

  ```csharp
  public static class LarikDataContract
  {
      /// <summary>Semantic contract version, independent of AssemblyVersion.</summary>
      public const string Version = "1.1.0";
  }
  ```

- Bump the minor version for an additive member or a new `ProviderSettingKind` appended at the end;
  this host renders an unknown kind as its default text box, so the add-on keeps working.

- The range parser supports exactly `>=a.b <c.d`. Reject anything else. Compare against
  `LarikDataContract.Version`, not the assembly version.
- An incompatible add-on is filtered **before download** and shown as
  "Requires Larik 0.5 or newer" rather than failing in `Activator.CreateInstance`.

### 7.4 Publishing the contract package

`Larik.Data` publishes from this repository with **Trusted Publishing (OIDC)**, not a long-lived API
key. One-time setup:

- nuget.org → **Trusted Publishing** → new policy: Repository Owner `dwifuady`, Repository
  `larik-winui`, Workflow File `publish-contract.yml`, and a scope that allows new packages matching
  `Larik.Data` (or `*` for the first publish).
- GitHub → Settings → Secrets and variables → Actions → **Variables** → `NUGET_USER` = the nuget.org
  profile name (not the email).

`.github/workflows/publish-contract.yml` requests `id-token: write`, exchanges it for a one-hour key
with `NuGet/login@v1`, and pushes. A new policy is temporarily active for 7 days; the first
successful publish locks it to the repository and owner permanently. If the window lapses, reactivate
it from the Trusted Publishing page.

### 7.5 Failure isolation

There is none: a provider that throws, crashes the process or corrupts native state takes the
app with it. Mitigations:

- Load each add-on in a `try`/`catch` and record a `LoadError`; never let one prevent the others
  or the core from starting.
- Resolve providers lazily on first connect, not at startup, so a broken add-on only affects the
  workspace that uses it.
- Surface a "Disable add-on" action that writes a `disabled` flag; disabled add-ons are skipped
  on the next start. This is the only safe recovery from a startup-time crash loop.

Out-of-process hosting is the real isolation answer and is explicitly out of scope. Do not
describe add-ons as sandboxed.

---

## 8. Install, update, remove

### 8.1 Install

1. Fetch and verify the index (signature, then entry).
2. Filter by `platforms`, `contract`, `minAppVersion`, and the channel allow-list.
3. Download to `Addons\download\<id>-<version>.zip.tmp`; enforce `sizeBytes` as a ceiling.
4. Verify SHA-256 **while streaming**; abort and delete on mismatch.
5. Extract to `Plugins\<id>\.staging-<version>\` with a path check:

   ```csharp
   var root = Path.GetFullPath(staging);
   foreach (var entry in archive.Entries)
   {
       var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
       if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
           throw new InvalidDataException("Add-on contains a path outside its folder.");
   }
   ```

   Reject absolute paths, `..`, reparse points and entries above a size cap.
6. Remove Mark-of-the-Web from the extracted DLLs (`Unblock-File` equivalent) so Windows does
   not refuse to load them.
7. Move `.staging-<version>` to `<version>\` atomically (same volume).
8. Write `current.json` with the new version.
9. Ask for a restart. Do **not** load the assembly in the running process.

### 8.2 Update

Same flow into a new version folder, then flip `current.json`. Keep the previous version on disk
so a failed start can roll back:

- If the app starts and the active version fails to load, rewrite `current.json` to the previous
  version and mark the new one `blocked` in a small sidecar.
- A successful start clears `blocked` for the version that loaded.

### 8.3 Remove

Delete the version folder(s), then the add-on folder. If the add-on might be loaded, require a
restart first. A workspace whose `provider_id` was served by the removed add-on keeps its saved
profile; it shows the not-installed state and offers reinstall.

### 8.4 Offline / enterprise

"Install from zip" is a first-class action: pick a zip, run the same verify/extract path minus
the download and index checks, and show the manifest metadata. This is the path for blocked
GitHub, air-gapped machines, and testing. A self-hosted index is a URL override, not a fork.

---

## 9. Channels and the Microsoft Store

### 9.1 Two channels

| Channel | Used by | Index `channel` | Third-party entries |
|---|---|---|---|
| `official` | Microsoft Store MSIX | `official` | Rejected |
| `all` | Installer, portable ZIP, winget | `all` | Allowed after review |

The channel is a compile-time constant (`LarikAddonChannel.Store`) set by the Store build
pipeline, with `LARIK_ADDON_CHANNEL` as a test-only override. The app rejects an index whose
`channel` is not in its allow-list, so a Store build cannot be pointed at the open index by
editing a config file.

Start with `official` in the Store. Flip it to `all` only after the Store has accepted one
release with the add-on system present.

### 9.2 What changes in the Store build

- **No installer components.** MSIX installs the whole package. A Store customer cannot untick
  MongoDB; optional providers must be add-ons (or MSIX optional packages, §9.4).
- **Install path is read-only.** `WindowsApps` cannot host plugins; everything goes to the
  package's `LocalState`, as designed.
- **Uninstall removes add-ons.** Reinstall reads the cached index and offers the previously
  installed set.
- **Updates are independent.** A Store update can raise `LarikDataContract.Version` past an
  installed add-on's range. The app must show "incompatible — update the add-on" before
  attempting to load, never throw from discovery.

### 9.3 Store policy mapping

Policies referenced below are from Microsoft Store Policies 7.20 (published 2026-09-15,
effective 2026-10-22). Re-check the current version before each submission.

| Policy | Text (paraphrased) | How this design complies |
|---|---|---|
| 10.1.5 | With user consent, after the initial download, the product may enable acquisition of add-ons or extensions that enhance its functionality. | Adding a database engine is an add-on that enhances the product; the UI asks before downloading. |
| 10.2.2 | Must not "fundamentally change or extend its described functionality … through any form of dynamic inclusion of code." | The provider system is described functionality: the listing names add-on database providers, and the app only loads assemblies that implement the documented contract. It does not download scripts or new capabilities outside that description. |
| 10.2.3 | Must not offer to install secondary software not developed by you that does **not** enhance functionality. | Every add-on implements `IDatabaseProvider` and therefore enhances the product; the `official` channel allows only first-party entries at launch. |
| 10.2.4 | May depend on non-integrated software to deliver primary functionality if the dependency is disclosed at the start of the description. | The listing's first paragraph states that database providers are add-ons and that the Store package includes SQL Server and PostgreSQL. |
| 10.2.5 | Products submitted as supported package types must be installed and updated only through the Store. | Applies to game products and Xbox; Larik is a non-game PC app. |
| 10.8.1 | Non-game PC products may use the Store IAP or a secure third-party purchase API for in-app purchases. | Add-ons are free in v1. If paid, use one of those APIs and disclose it in Partner Center. |

**Not a storefront.** 10.1.6's storefront requirements (20+ products, 18+ rating) do not apply:
the list is add-ons for this product, not a catalogue of unrelated digital goods.

### 9.4 Store-native alternatives and why they are not the plan

- **MSIX optional packages with executable code** are supported, but must be part of a *related
  set* and require Partner Center permission to submit. They are publisher-controlled, so they
  fit first-party providers and cannot host third-party ones. Useful later for a first-party
  "PostgreSQL pack" if bundling ever becomes a size problem; not the basis of a curated index.
- **App extensions** cannot load code (Microsoft docs: "Applications cannot load code from
  extensions"). Content only.
- **Modification packages** are an enterprise/IT mechanism, not a Store distribution path.

### 9.5 Certification notes template

Paste into Partner Center's **Notes for certification**:

> Larik is a SQL studio. This package includes the SQL Server and PostgreSQL providers and works
> fully offline. MongoDB and future engines are optional add-ons, offered in the app's documented
> Add-ons page. Add-ons come from a curated, first-party index at `<URL>`; this build accepts
> only first-party (`official`) entries. Each add-on is a .NET assembly that implements the
> app's provider contract, is verified by its SHA-256 before installation (the index itself is not
> yet signed, but this build only reads first-party entries), is
> installed to the user's LocalAppData, and requires a restart. Add-ons add a database engine;
> they do not change the app's primary functionality. Add-ons can be removed in-app. No
> add-on installs scripts or executable code outside the app process.

Keep it truthful. If the published build allows third parties, say so and name the review
process.

---

## 10. UX states

The add-on system adds exactly one new surface: **Settings → Add-ons**, plus a contextual entry
point where a missing provider is needed. Per the repo's decision-load rules, the default path
must require no decision.

| State | Where | Copy |
|---|---|---|
| Not installed | Workspace connect / add-on page | "PostgreSQL isn't installed. **Install add-on**" |
| Downloading | Add-on page, inline | Progress + cancel; disable the action |
| Incompatible | Add-on page | "Requires Larik 0.6 or newer. **Update Larik**" |
| Corrupt | Add-on page | "The download failed verification. **Try again**" |
| Load failed | Connection dialog, existing load-errors area | "MongoDB 1.0.0: <message>. **Disable**" |
| Update available | Add-on page | Quiet badge, not a modal |
| Restart required | Add-on page / status bar | "Restart Larik to finish installing MongoDB." |

Rules:

- Never auto-install on launch.
- One primary action per state; no settings for the index URL in normal UI (env/test override
  only).
- A missing provider must **never** surface the raw
  `No database provider is loaded for 'postgres'.` exception; `ConnectionViewModel.ConnectAsync`
  already throws it, so map it to the not-installed state.
- Status is text + colour; the disabled state is not colour-only.

---

## 11. Security and trust

- Add-ons are **trusted, in-process code**. Approval is a review claim, not a security boundary.
  Say that in the Add-ons page.
- Verify `sha256` and the index signature before extraction.
- Reject unsigned or unlisted add-ons in the `official` channel; `all` still requires the index.
- Signing (Authenticode) of add-on DLLs is recommended as soon as there are third parties:
  Windows Smart App Control and some WDAC policies can block unsigned DLLs loaded from
  user-writable paths. Test a Store build on an SAC-enabled machine.
- Never log connection strings or secrets from an add-on; the host already owns
  `SecretStore`. Add-ons receive a `ConnectionProfile` with secrets supplied at connect time.
- The `Plugins` folder is user-writable by design. A malicious local process can drop a DLL
  there regardless of the store; the index reduces accidental risk, not local privilege.

---

## 12. Tests

Unit (`Larik.Data.Tests`):

- Contract range parser accepts `>=1.0 <2.0`, rejects malformed ranges.
- Index deserialization + unknown `schemaVersion` rejection.
- Signature verification with a test key.
- Zip path-traversal rejection (absolute, `..`, reparse).
- Hash mismatch aborts and leaves no `current.json`.
- Host-binding test: a probe add-on implementing `IDatabaseProvider` loaded in `AddonLoadContext`
  is discoverable by the catalogue (guards the shared-contract rule).

UI (`ui-tests.ps1`):

- Add-on page lists the cached index; "Install from zip" installs a fixture add-on and the
  provider appears in the new-workspace dialog after restart.
- Removing the add-on returns the workspace to the not-installed state with an install action.
- An incompatible fixture shows the "requires newer Larik" state, not a crash.
- A corrupt fixture reports verification failure and installs nothing.

Keep fixtures tiny and offline. Do not make CI depend on GitHub.

---

## 13. Open decisions

1. **PostgreSQL in core or add-on?** Recommendation: keep it bundled for the Store launch.
   It is a headline engine, costs ~2 MB, and bundling keeps the default experience free of
   dynamic code. MongoDB is the first add-on: it is the largest dependency tree (~10 MB managed
   plus native crypto), the most optional engine, and already documented as niche. Revisit only
   if the installer size becomes a real complaint.
2. **Index signing library.** ECDSA P-256 over a canonical JSON projection is enough; avoid a
   new dependency if `System.Security.Cryptography` covers it. Ed25519 is nicer if the BCL
   version in use supports it.
3. **Cache lifetime.** Daily refresh is a guess; measure. A stale index should never block a
   working app.
4. **Whether `official` ever opens to third parties.** Decide after one Store certification
   cycle, with the review checklist exercised at least once.
5. **Paid add-ons.** Out of scope until there is a support and refund story. If it happens,
   Store IAP for the Store build and a secure third-party API elsewhere, disclosed in Partner
   Center.

---

## 14. References

- [`PROVIDERS.md`](PROVIDERS.md) — the contract, compatibility rules and current loader limits.
- [Microsoft Store Policies 7.20](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies-and-code-of-conduct)
- [Optional packages and related sets](https://learn.microsoft.com/en-us/windows/msix/package/optional-packages)
- [App package formats](https://learn.microsoft.com/en-us/windows/msix/package/app-package-formats)
- [NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
- [.NET `AssemblyLoadContext`](https://learn.microsoft.com/en-us/dotnet/core/dependency-loading/understanding-assemblyloadcontext)

