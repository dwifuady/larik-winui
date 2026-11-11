# Notes for certification — Larik SQL Studio 0.1.0

Paste into Partner Center → Submission → Notes for certification.

---

Larik SQL Studio is a SQL developer tool. It stores workspaces, tabs and SQL locally and connects
only to database servers the user configures.

**No account is required.** There is no sign-in, no telemetry and no background service. The app
runs full-trust but makes no network call until the user connects to a database.

**Test path (no server needed):**

1. Launch the app. A Demo workspace is available.
2. Connect it and run any query (`Ctrl+Enter`) to see results in the grid.
3. Open About to confirm the version (`0.1.0`).

**Built-in providers only.** This edition includes SQL Server and PostgreSQL, plus an in-memory Demo
database. It does not download, unpack or load any add-on code, does not scan a Plugins folder, and
has no store or index of extensions. There is no Settings → Drivers page and no
"Install another database driver…" action in this build; missing drivers report "Not included in
this edition".

**No self-update.** Packaged and Store builds do not contact GitHub for updates. Updates are
delivered by the Microsoft Store, and About says so.

**Offline.** A bare machine with no SQL Server and no PostgreSQL installed can still use the Demo
database. That is the recommended path if the test VM has no database server.

**Data location.** Workspaces, tabs and SQL live in the package's own local app data
(`LocalState`). Connection profiles are stored locally; passwords are never written to disk and go
to Windows Credential Manager. A user who has used another distribution of Larik will not see those
workspaces here, and nothing is migrated.

**SQL Server testing (optional).** SQL Server LocalDB or any reachable SQL Server instance works;
PostgreSQL needs a server. Neither is required to review the app.

Contact: _TODO: support email_
