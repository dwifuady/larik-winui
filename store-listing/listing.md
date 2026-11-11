# Microsoft Store listing — Larik SQL Studio 0.1.0

Everything below is copy-ready for Partner Center. Do not add MongoDB, add-ons or the GitHub
build to any of it: the Store package has none of that surface.

## At a glance

| Field | Value |
|---|---|
| Product name | Larik SQL Studio |
| Category | Developer tools |
| Pricing | Free |
| Markets | All available markets |
| System requirements | Windows 10 1809 or later, x64 |
| Package identity | `fdydw.LarikSQLStudio` 0.1.0.0 |
| Publisher | `CN=CE4DFFA4-7660-4B31-9066-6E8B0C1CF323` |

## Short description (≤270 characters)

> Larik is a native Windows SQL studio for SQL Server and PostgreSQL. Write queries with live
> highlighting and completion, run each tab in its own session, and browse results in a fast grid.
> Workspaces keep connections, tabs and SQL together. A built-in Demo database works offline.

## Long description (≤10,000 characters)

**A SQL studio that behaves like a Windows app.**

Larik is a native SQL studio for SQL Server and PostgreSQL, built with WinUI 3 and the Windows App
SDK. It uses Mica and your system theme, follows Windows keyboard and accessibility conventions, and
does not put a web page inside a window.

**Workspaces are the object.** Each workspace owns one connection. Its databases sit underneath it,
and its query tabs live beside it. A workspace starts disconnected and connects from the rail with
one action. Workspaces carry a colour and an icon so you can tell them apart at a glance, and you can
hide or reorder them. Deleting a workspace archives its SQL first.

**Vertical tabs, the way a studio should work.** Query tabs stack vertically and can be grouped,
pinned, renamed inline and reordered. Closing a tab is non-destructive: the SQL moves to an archive
you can reopen, with optional auto-archive.

**An editor that knows your schema.** Syntax highlighting comes from the database dialect, so T-SQL
and PostgreSQL each look right. Completion understands the clause you are in and offers tables,
columns, aliases, joins, CTEs, variables, databases and procedure calls. Snippets expand on Tab.
`Ctrl+K` opens a schema peek that pins beside the editor while you type.

**Execution with a real session.** `Ctrl+Enter` runs the statement under the caret. `F5` runs the
document or the selection and splits `GO` batches. Stop cancels a running query without closing the
session. `PRINT` output and warnings open in a Messages tab.

Each tab owns one connection session. Temp tables, `SET` options and an open `BEGIN TRAN` stay on
that tab and never leak into another. A transaction guard offers Commit and Roll back, and `USE`
moves the tab, the rail and completion to the new database together.

**Results that stay usable.** Every result set gets its own tab. The grid is virtualized, cells open
in a preview with JSON and XML formatting, foreign-key values show the row they reference, and you can
copy rows as TSV or export CSV and JSON.

**Connections without ceremony.** SQL Server and PostgreSQL are built in. A connection dialog is
generated from the provider's own settings, Test reports inline, and credentials stay in Windows
Credential Manager rather than in a settings file. A built-in Demo database needs no server at all,
so you can try the editor, tabs and results offline on a fresh machine.

**Local by default.** Workspaces, tabs and SQL are stored locally in this app's own data folder.
Larik does not collect telemetry. The only network connections it makes are the ones you configure:
connections to your own database servers. This edition does not check for updates over the network;
updates arrive through the Microsoft Store.

**Requirements.** Windows 10 version 1809 or later, x64. SQL Server and PostgreSQL are optional: the
Demo database works with no server installed.

*Note: this edition stores workspaces in its own application data. Content created in another
distribution of Larik is not imported or migrated.*

## Keywords

`SQL`, `SQL Server`, `PostgreSQL`, `query editor`, `database`, `T-SQL`, `developer tools`

## Screenshots

All captured from the Store build (`-c Store`) at 1440×900, in `store-listing/screenshots/`.
Partner Center accepts 4–6; the first four are the set.

| File | Caption (≤200 chars) |
|---|---|
| `01-query-results.png` | Workspaces own one connection, and each query tab keeps its own session. Results stay beside the SQL that produced them. |
| `02-completion.png` | Clause-aware completion suggests tables, aliases and columns as you type — Tab to accept. |
| `03-settings-snippets.png` | Built-in SQL snippets you can edit or disable, scoped per dialect, plus your own. |
| `04-appearance.png` | Follow the Windows theme, or pin Light or Dark. Open tabs redraw immediately. |

Not used in the listing: `about.png` (internal only — it shows a local file path, which must not
appear in a public listing; it is the evidence that About reads 0.1.0 and that updates come from the
Store).

The transaction screenshot is still missing: the Demo provider does not implement interactive
transactions (`begin tran` returns "No table in this statement"), so it needs a SQL Server LocalDB
workspace.

## Support contact

dwifuady@hotmail.com

## Privacy policy URL

https://dwifuady.github.io/larik-site/privacy/

## Age rating

IARC questionnaire: developer tool, no objectionable content. Answer **No** to every content
category (violence, sexuality, language, drugs, gambling, horror, user-generated content sharing,
location sharing, personal information sharing, in-app purchases, ads). Add the note:

> Larik displays data from databases the user connects to. It does not host or distribute user
> content, and it has no social, chat or sharing features.
