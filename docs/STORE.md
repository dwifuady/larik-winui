# Store edition and submission

The Microsoft Store package is the **built-in edition**: SQL Server, PostgreSQL and the in-memory
Demo. It does not offer add-ons, does not scan `Plugins`, and never downloads an assembly.

The GitHub build is the **extended edition**: the same core plus Settings → Drivers and the curated
index (MongoDB today). Both come from this repository; the difference is the build configuration.

---

## 1. What the code does

| Piece | Store build |
|---|---|
| Configuration | `-c Store` |
| Constant | `LARIK_STORE` |
| `AddonPolicy.AddonsEnabled` | `false` |
| `DriverRuntime.Initialize` | built-ins only; no `Plugins` scan |
| Settings → Drivers | hidden |
| “Install another database driver…” | hidden |
| Missing driver copy | “Not included in this edition” |
| Package | x64 MSIX, self-contained, unsigned (the Store signs it) |
| Index | `official.json` exists for a future signed first-party channel; unused today |

### Version and identity

The workflow takes **one** version input, three or four parts:

- `1.0.0` → package identity `1.0.0.0`, app version `1.0.0`
- `1.0.0.0` → package identity `1.0.0.0`, app version `1.0.0`

The package needs four parts; the About page and assembly metadata use the first three. Before the
artifact is uploaded, `scripts/verify-store-package.ps1` opens the MSIX and fails unless:

- `Identity Name` equals `STORE_IDENTITY_NAME`
- `Publisher` equals `STORE_PUBLISHER` (a bare name is normalized to `CN=...`)
- `Version` equals the normalized four-part version

The script also rejects the development values (`5F1C2F1B-…`, `CN=AppPublisher`) so a Store package
cannot be built with the placeholder identity.

The Store manifest is generated from `Larik/Package.appxmanifest` by
`scripts/prepare-store-manifest.ps1`, which fills the identity and publisher from Partner Center and
drops the dev placeholders. `Larik.csproj` uses that generated file only for `-c Store`.

Build it locally:

```powershell
pwsh ./scripts/prepare-store-manifest.ps1 `
    -IdentityName '<Identity Name>' `
    -Publisher 'CN=<Publisher>' `
    -PublisherDisplayName 'Larik' `
    -Version 1.0.0.0

dotnet publish Larik/Larik.csproj `
    -c Store -r win-x64 -p:Platform=x64 `
    -p:GenerateAppxPackageOnBuild=true `
    -p:AppxPackageSigningEnabled=false `
    -p:WindowsPackageType=MSIX `
    -p:AppxBundle=Never `
    -p:SelfContained=true `
    -p:WindowsAppSDKSelfContained=true
```

The package lands in `Larik/AppPackages/Larik_<version>_x64_Store_Test/`.

For a **behaviour** check without installing, `winapp run . -c Store` launches the Store
configuration with a debug identity. For a real install test, sign a **dev** package
(`winapp cert generate`, `winapp package --cert`) or use the Store's test flight; the submission
artifact itself stays unsigned.

---

## 2. What you must do in Partner Center

1. **Create a developer account** at <https://storedeveloper.microsoft.com>. Individual and company
   registration are free.
2. **Reserve the app name** and copy these three values:
   - **Identity Name** (Partner Center → App identity; often a GUID or `Publisher.AppName`)
   - **Publisher** (a DN such as `CN=...`; it must match the identity you use to sign)
   - **Publisher display name**
3. In GitHub → Settings → Secrets and variables → Actions → **Variables**, add:
   - `STORE_IDENTITY_NAME`
   - `STORE_PUBLISHER`
   - `STORE_PUBLISHER_DISPLAY_NAME`
4. Run **Actions → Store package → Run workflow** with a version:
   - `1.0.0` for the first submission (the package becomes `1.0.0.0`);
   - increase it for every later submission; the Store rejects a version that does not advance.
   The workflow fails if the variables are missing, if the publisher is still the dev placeholder,
   or if the built MSIX does not carry the expected identity, publisher and version. Download the
   `larik-store-msix` artifact.
5. **Test the package** on a clean Windows 11 VM:
   - install the package (dev-signed build or Store test flight);
   - confirm launch, license/identity, `%LOCALAPPDATA%` redirection, and the Drivers page is absent;
   - ideally with **Smart App Control enabled** to prove there is no unsigned code path.
6. Complete the **Store listing**:
   - category: Developer tools;
   - description: “SQL Server and PostgreSQL studio” — do **not** advertise add-ons or MongoDB;
   - screenshots, support contact, privacy policy URL, age rating;
   - pricing: free (or your choice);
   - system requirements: Windows 10 1809+.
7. **Notes for certification** (paste and adapt):

   > Larik is a SQL studio. This build includes the SQL Server and PostgreSQL providers and works
   > offline with the in-memory Demo provider. It does not download or load any add-on code. The
   > provider system is part of the app's documented functionality, but the Store edition is
   > built-ins only. No telemetry is collected; connection profiles are stored locally and
   > passwords use Windows Credential Manager. The app runs full-trust and uses no background
   > services.

8. **Submit**, watch certification, fix anything it reports, and resubmit.
9. **Updates**: increase the four-part package version, rerun the workflow, and submit the new
   MSIX.

---

## 3. Do not do this

- Do not mention MongoDB, add-ons, or the GitHub build in the Store listing. The Store package must
  match its description; the GitHub edition is a separate distribution with its own risks.
- Do not claim the Store signature covers downloaded drivers. It does not.
- Do not put the Store build on the open index. `AddonPolicy` already prevents it; keep it that way
  until signed first-party add-ons exist.

---

## 4. State continuity

The Store MSIX writes to the package's `LocalCache`; the GitHub build writes to
`%LOCALAPPDATA%\Larik WinUI`. A user who tries both sees two different stores unless the one-time
import from the profile store to the package store is present and verified in the Store package
before submission. Test this with a state that contains at least one workspace, one tab and one
saved connection.

---

## 5. Signing the GitHub build (separate)

The Store edition is signed by Microsoft. The GitHub installer and first-party add-on DLLs are not.
If you want SmartScreen reputation and Smart App Control compatibility for those:

- **Azure Artifact Signing** (formerly Trusted Signing): ~$9.99/month Basic, identity validation
  required;
- **OV certificate**: ~$220–$400/year plus hardware/cloud key storage;
- **EV**: only for a registered legal entity, and no longer the SmartScreen shortcut it once was.

Store signing does not cover add-ons. If you later offer MongoDB in the Store, bundle it in the
MSIX (Microsoft signs it) or sign the add-on DLLs and add them to `official.json`.
