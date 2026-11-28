using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Larik.Models;
using Microsoft.Data.Sqlite;

namespace Larik.Services;

/// <summary>The workspace session: spaces, their connections and tabs.</summary>
public sealed class StudioState
{
    public List<SpaceState> Spaces { get; set; } = [];

    public string? ActiveSpaceId { get; set; }

    public int QueryCounter { get; set; }

    /// <summary>Archived tabs, newest first. Archived tabs can be restored into their workspace.</summary>
    public List<ArchivedTabState> ArchivedTabs { get; set; } = [];

    public bool AutoArchiveEnabled { get; set; } = true;

    public int AutoArchiveDays { get; set; } = 30;

    /// <summary>Whether the notes pane is docked on the right; the notes themselves live in their own table.</summary>
    public bool NotesPaneOpen { get; set; }

    /// <summary>Last window geometry, restored on launch; null on the first run or after a reset.</summary>
    public WindowBoundsState? WindowBounds { get; set; }

    /// <summary>Height of the results pane in DIPs, restored the next time a query opens it.</summary>
    public double? ResultsPaneHeight { get; set; }

    /// <summary>Width of the cell-preview pane in DIPs.</summary>
    public double? CellPreviewWidth { get; set; }

    /// <summary>The saved appearance choice: <c>system</c>, <c>light</c> or <c>dark</c>.</summary>
    public string Theme { get; set; } = "system";

    /// <summary>
    /// Whether the GitHub edition checks Releases once per launch. Packaged builds never check,
    /// regardless of this preference.
    /// </summary>
    public bool CheckForUpdates { get; set; } = true;
}

/// <summary>
/// The window rectangle in device-independent pixels, so a DPI change between launches does not
/// stretch or shrink the restored window. <see cref="Maximized"/> is remembered separately from
/// the rectangle: restoring a maximized window must not make its restore-down size the monitor.
/// </summary>
public sealed class WindowBoundsState
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public bool Maximized { get; set; }
}

/// <summary>One space, including the connection profile it owns (passwords are never stored).</summary>
public sealed class SpaceState
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Color { get; set; } = "#C4B5FD";

    /// <summary>A <see cref="SpaceIcons"/> name; null means the workspace shows its first letter.</summary>
    public string? Icon { get; set; }

    /// <summary>Hidden from the rail and switcher, but kept intact on the Workspaces page.</summary>
    public bool Hidden { get; set; }

    public string ProviderId { get; set; } = "mssql";

    public Dictionary<string, string> Profile { get; set; } = [];

    public string? ActiveDatabase { get; set; }

    public string? ActiveTabId { get; set; }

    public List<TabState> Tabs { get; set; } = [];

    /// <summary>Groups in display order; a group is a folder when <see cref="TabGroupState.Pinned"/>.</summary>
    public List<TabGroupState> Groups { get; set; } = [];
}

/// <summary>A named set of tabs. Pinned groups render in the pinned band as folders.</summary>
public sealed class TabGroupState
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Color { get; set; } = "#C4B5FD";

    public bool Pinned { get; set; }

    public bool Expanded { get; set; } = true;

    /// <summary>The database the group was auto-created from; new tabs on it join the group.</summary>
    public string? Database { get; set; }
}

/// <summary>One tab: its title, its database subtitle and the SQL it holds.</summary>
public sealed class TabState
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Database { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public bool Pinned { get; set; }

    /// <summary>Last selection/edit time; auto-archive measures inactivity from it.</summary>
    public DateTimeOffset? LastAccessedAt { get; set; }

    /// <summary>The group this tab belongs to; null for a loose tab.</summary>
    public string? GroupId { get; set; }
}

/// <summary>A tab kept in the archive/history page after it left its workspace.</summary>
public sealed class ArchivedTabState
{
    public string Id { get; set; } = string.Empty;

    public string SpaceId { get; set; } = string.Empty;

    public string SpaceName { get; set; } = string.Empty;

    public string Color { get; set; } = "#C4B5FD";

    public string Title { get; set; } = string.Empty;

    public string Database { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public bool Pinned { get; set; }

    public DateTimeOffset ArchivedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The group the tab was in, so a restore returns it to the same place.</summary>
    public string? GroupName { get; set; }
}

/// <summary>Reads the pre-SQLite <c>studio.json</c> state once, so upgrades keep their spaces.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(StudioState))]
[JsonSerializable(typeof(WindowBoundsState))]
internal sealed partial class StudioJsonContext : JsonSerializerContext
{
}

/// <summary>
/// The workspace session persisted between launches, in the same shape as the desktop app's
/// storage (spaces / tabs / app_state). Lives in the package's <c>LocalState</c> for a packaged
/// run, or <c>%LOCALAPPDATA%\Larik WinUI</c> for an unpackaged one (<see cref="LarikPaths"/>).
/// (<c>larik-winui-dev.db</c> for debug builds) so the WinUI port and the Tauri app never open
/// each other's incompatible store.
/// <para>
/// Writes are best-effort: a broken or unreadable database opens the first-run welcome surface
/// instead of failing the launch. Provider-declared password fields are never written.
/// </para>
/// </summary>
public sealed partial class StudioStore : IVirtualReferenceStore
{
    private const int SchemaVersion = 11;

    public StudioStore()
        : this(LarikPaths.DatabasePath)
    {
    }

    public StudioStore(string databasePath) => DatabasePath = databasePath;

    public string DatabasePath { get; }

    public StudioState? Load()
    {
        try
        {
            using var connection = Open();
            var state = Read(connection);
            if (state.Spaces.Count > 0)
            {
                return state;
            }

            return TryImportLegacyJson(connection);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads only the appearance choice, before the first window exists. The app theme has to be
    /// assigned during startup, so loading the full workspace state here would be wasted work.
    /// </summary>
    public AppTheme LoadThemePreference()
    {
        try
        {
            using var connection = Open();
            return AppThemeText.Parse(ReadState(connection, "theme"));
        }
        catch
        {
            return AppTheme.System;
        }
    }

    public void Save(StudioState state)
    {
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            Write(connection, transaction, state);
            transaction.Commit();
        }
        catch
        {
            // Persistence must never take the studio down.
        }
    }

    // ---------------------------------------------------------------- custom references

    /// <summary>User-defined references for one workspace + database, oldest first.</summary>
    public IReadOnlyList<VirtualReferenceRecord> GetVirtualReferences(string spaceId, string databaseName)
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, space_id, database_name,
                       source_schema, source_table, source_column,
                       target_schema, target_table, target_column
                FROM virtual_references
                WHERE space_id = $space AND database_name = $database COLLATE NOCASE
                ORDER BY source_table, source_column;
                """;
            command.Parameters.AddWithValue("$space", spaceId);
            command.Parameters.AddWithValue("$database", databaseName);

            var references = new List<VirtualReferenceRecord>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                references.Add(new VirtualReferenceRecord
                {
                    Id = reader.GetString(0),
                    SpaceId = reader.GetString(1),
                    DatabaseName = reader.GetString(2),
                    SourceSchema = reader.GetString(3),
                    SourceTable = reader.GetString(4),
                    SourceColumn = reader.GetString(5),
                    TargetSchema = reader.GetString(6),
                    TargetTable = reader.GetString(7),
                    TargetColumn = reader.GetString(8),
                });
            }

            return references;
        }
        catch
        {
            // A broken reference store must not hide query results or the grid.
            return [];
        }
    }

    /// <summary>
    /// Saves a reference, replacing any existing one for the same source column. Source identifiers
    /// are compared case-insensitively, matching how the schema reports them.
    /// </summary>
    public bool SaveVirtualReference(VirtualReferenceRecord reference)
    {
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            Execute(
                connection,
                transaction,
                """
                DELETE FROM virtual_references
                WHERE space_id = $space
                  AND database_name = $database COLLATE NOCASE
                  AND source_schema = $sourceSchema COLLATE NOCASE
                  AND source_table = $sourceTable COLLATE NOCASE
                  AND source_column = $sourceColumn COLLATE NOCASE;
                """,
                ("$space", reference.SpaceId),
                ("$database", reference.DatabaseName),
                ("$sourceSchema", reference.SourceSchema),
                ("$sourceTable", reference.SourceTable),
                ("$sourceColumn", reference.SourceColumn));

            Execute(
                connection,
                transaction,
                """
                INSERT INTO virtual_references
                    (id, space_id, database_name, source_schema, source_table, source_column,
                     target_schema, target_table, target_column)
                VALUES ($id, $space, $database, $sourceSchema, $sourceTable, $sourceColumn,
                        $targetSchema, $targetTable, $targetColumn);
                """,
                ("$id", reference.Id),
                ("$space", reference.SpaceId),
                ("$database", reference.DatabaseName),
                ("$sourceSchema", reference.SourceSchema),
                ("$sourceTable", reference.SourceTable),
                ("$sourceColumn", reference.SourceColumn),
                ("$targetSchema", reference.TargetSchema),
                ("$targetTable", reference.TargetTable),
                ("$targetColumn", reference.TargetColumn));

            transaction.Commit();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool DeleteVirtualReference(string id)
    {
        try
        {
            using var connection = Open();
            return Execute(connection, null, "DELETE FROM virtual_references WHERE id = $id;", ("$id", id)) > 0;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- connection + schema

    private SqliteConnection Open()
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            // A packaged app that opens with ReadWriteCreate creates a private copy even when the
            // unvirtualized AppData file exists. ReadWrite lets the OS fall back to the existing
            // file, so packaged and unpackaged launches can share one store.
            Mode = File.Exists(DatabasePath)
                ? SqliteOpenMode.ReadWrite
                : SqliteOpenMode.ReadWriteCreate,
        }.ToString());

        connection.Open();
        Execute(
            connection,
            null,
            """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            """);

        EnsureSchema(connection);
        return connection;
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        var version = Convert.ToInt32(Scalar(connection, null, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version >= SchemaVersion)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        Execute(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS spaces (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                color TEXT,
                icon TEXT,
                provider_id TEXT NOT NULL DEFAULT 'mssql',
                hidden INTEGER NOT NULL DEFAULT 0,
                active_database TEXT,
                last_active_tab_id TEXT,
                sort_order INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS space_settings (
                space_id TEXT NOT NULL,
                key TEXT NOT NULL,
                value TEXT NOT NULL,
                PRIMARY KEY (space_id, key),
                FOREIGN KEY (space_id) REFERENCES spaces(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS tabs (
                id TEXT PRIMARY KEY,
                space_id TEXT NOT NULL,
                title TEXT NOT NULL,
                database TEXT,
                content TEXT,
                pinned INTEGER NOT NULL DEFAULT 0,
                last_accessed_at TEXT,
                sort_order INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at TEXT NOT NULL DEFAULT (datetime('now')),
                FOREIGN KEY (space_id) REFERENCES spaces(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS archived_tabs (
                id TEXT PRIMARY KEY,
                space_id TEXT NOT NULL,
                space_name TEXT NOT NULL,
                color TEXT,
                title TEXT NOT NULL,
                database TEXT,
                content TEXT,
                pinned INTEGER NOT NULL DEFAULT 0,
                archived_at TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS tab_groups (
                id TEXT PRIMARY KEY,
                space_id TEXT NOT NULL,
                name TEXT NOT NULL,
                color TEXT,
                pinned INTEGER NOT NULL DEFAULT 0,
                expanded INTEGER NOT NULL DEFAULT 1,
                database TEXT,
                sort_order INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (space_id) REFERENCES spaces(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS app_state (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                updated_at TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS virtual_references (
                id TEXT PRIMARY KEY,
                space_id TEXT NOT NULL,
                database_name TEXT NOT NULL,
                source_schema TEXT NOT NULL DEFAULT '',
                source_table TEXT NOT NULL,
                source_column TEXT NOT NULL,
                target_schema TEXT NOT NULL DEFAULT '',
                target_table TEXT NOT NULL,
                target_column TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at TEXT NOT NULL DEFAULT (datetime('now')),
                FOREIGN KEY (space_id) REFERENCES spaces(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_spaces_sort_order ON spaces(sort_order);
            CREATE INDEX IF NOT EXISTS idx_tabs_space_id ON tabs(space_id, sort_order);
            CREATE INDEX IF NOT EXISTS idx_tab_groups_space_id ON tab_groups(space_id, sort_order);
            CREATE INDEX IF NOT EXISTS idx_archived_tabs_archived_at ON archived_tabs(archived_at DESC);
            CREATE INDEX IF NOT EXISTS idx_virtual_references_scope
                ON virtual_references(space_id, database_name);

            -- Version 7: MCP exposure. The audit table is written by both the app and Larik.Mcp,
            -- which is why it lives in the same store (one audit list, one source of truth).
            CREATE TABLE IF NOT EXISTS mcp_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                recorded_at TEXT NOT NULL DEFAULT (datetime('now')),
                tool TEXT NOT NULL,
                space_id TEXT,
                space_name TEXT,
                provider_id TEXT,
                access_level TEXT NOT NULL,
                statement TEXT,
                status TEXT NOT NULL,
                reason TEXT,
                duration_ms INTEGER NOT NULL DEFAULT 0,
                rows_returned INTEGER NOT NULL DEFAULT 0,
                decision_surface TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_mcp_log_recorded_at ON mcp_log(recorded_at DESC);

            CREATE TABLE IF NOT EXISTS mcp_approvals (
                id TEXT PRIMARY KEY,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                space_id TEXT NOT NULL,
                space_name TEXT NOT NULL,
                statement TEXT NOT NULL,
                status TEXT NOT NULL,
                decided_at TEXT,
                surface TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_mcp_approvals_status ON mcp_approvals(status, created_at);

            -- Version 8: the run log. Executions, not decisions - mcp_log above stays the approval
            -- trail. Both processes write rows here, and rows are pruned on a retention window
            -- because statement text is the whole point of the table.
            CREATE TABLE IF NOT EXISTS run_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                recorded_at TEXT NOT NULL DEFAULT (datetime('now')),
                actor TEXT NOT NULL,
                space_id TEXT,
                space_name TEXT,
                database_name TEXT,
                provider_id TEXT,
                tab_id TEXT,
                statement TEXT NOT NULL,
                status TEXT NOT NULL,
                reason TEXT,
                duration_ms INTEGER NOT NULL DEFAULT 0,
                rows_returned INTEGER NOT NULL DEFAULT 0,
                row_limit INTEGER,
                is_truncated INTEGER NOT NULL DEFAULT 0,
                description TEXT,
                reference TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_run_log_recorded_at ON run_log(recorded_at DESC);
            CREATE INDEX IF NOT EXISTS idx_run_log_space
                ON run_log(space_id, recorded_at DESC);

            -- Version 10: notes. An anchor stores a range plus a text fingerprint, never a line
            -- number: the range rebases on edit and the fingerprint re-finds it on reload.
            CREATE TABLE IF NOT EXISTS notes (
                id TEXT PRIMARY KEY,
                space_id TEXT NOT NULL DEFAULT '',
                tab_id TEXT NOT NULL,
                kind TEXT NOT NULL DEFAULT 'note',
                body TEXT NOT NULL DEFAULT '',
                anchor_start INTEGER NOT NULL DEFAULT 0,
                anchor_length INTEGER NOT NULL DEFAULT 0,
                quote TEXT NOT NULL DEFAULT '',
                prefix TEXT NOT NULL DEFAULT '',
                suffix TEXT NOT NULL DEFAULT '',
                line_hint INTEGER NOT NULL DEFAULT 1,
                is_unanchored INTEGER NOT NULL DEFAULT 0,
                is_tab INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE INDEX IF NOT EXISTS idx_notes_tab ON notes(tab_id, anchor_start);
            CREATE INDEX IF NOT EXISTS idx_notes_space ON notes(space_id);
            """);

        // Version 2 adds pin state, the inactivity clock and the archive table. Version 3 adds the
        // workspace icon. Version 4 adds the rail visibility flag. Version 5 adds tab groups and
        // the archive's group name. Version 6 adds user-defined reference metadata. Version 7 adds
        // the MCP exposure level and the MCP audit/approval tables. Version 8 adds the run log.
        // Version 9 adds the agent's reason and ticket reference to a run.
        // Version 10 adds notes: an anchor with a text fingerprint, not a line number.
        // Version 11 adds tab notes: a scratchpad line for a document with no statement yet.
        EnsureColumn(connection, transaction, "tabs", "pinned", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, transaction, "tabs", "last_accessed_at", "TEXT");
        EnsureColumn(connection, transaction, "tabs", "group_id", "TEXT");
        EnsureColumn(connection, transaction, "archived_tabs", "group_name", "TEXT");
        EnsureColumn(connection, transaction, "spaces", "icon", "TEXT");
        EnsureColumn(connection, transaction, "spaces", "hidden", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, transaction, "spaces", "mcp_access", "TEXT NOT NULL DEFAULT 'off'");
        EnsureColumn(connection, transaction, "run_log", "description", "TEXT");
        EnsureColumn(connection, transaction, "run_log", "reference", "TEXT");
        EnsureColumn(connection, transaction, "notes", "is_tab", "INTEGER NOT NULL DEFAULT 0");

        Execute(connection, transaction, $"PRAGMA user_version = {SchemaVersion};");
        transaction.Commit();
    }

    /// <summary>Adds a column to an existing table when it predates the current schema.</summary>
    private static void EnsureColumn(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        string definition)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA table_info({table});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        Execute(
            connection,
            transaction,
            $"ALTER TABLE {table} ADD COLUMN {column} {definition};");
    }

    // ---------------------------------------------------------------- read

    private static StudioState Read(SqliteConnection connection)
    {
        var appState = ReadAllState(connection);
        appState.TryGetValue("active_space_id", out var activeSpaceId);
        appState.TryGetValue("query_counter", out var queryCounterRaw);
        appState.TryGetValue("auto_archive_enabled", out var autoArchiveEnabledRaw);
        appState.TryGetValue("auto_archive_days", out var autoArchiveDaysRaw);
        appState.TryGetValue("notes_pane_open", out var notesPaneOpenRaw);
        appState.TryGetValue("window_bounds", out var windowBoundsRaw);
        appState.TryGetValue("results_pane_height", out var resultsPaneHeightRaw);
        appState.TryGetValue("cell_preview_width", out var cellPreviewWidthRaw);
        appState.TryGetValue("theme", out var themeRaw);
        appState.TryGetValue("check_for_updates", out var checkForUpdatesRaw);

        var state = new StudioState
        {
            ActiveSpaceId = activeSpaceId,
            QueryCounter = int.TryParse(
                queryCounterRaw,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var counter)
                ? counter
                : 0,
            AutoArchiveEnabled = !bool.TryParse(autoArchiveEnabledRaw, out var enabled)
                || enabled,
            AutoArchiveDays = int.TryParse(
                autoArchiveDaysRaw,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var days)
                ? days
                : 30,
            NotesPaneOpen = bool.TryParse(notesPaneOpenRaw, out var notesPaneOpen)
                && notesPaneOpen,
            WindowBounds = ParseWindowBounds(windowBoundsRaw),
            ResultsPaneHeight = ParseDouble(resultsPaneHeightRaw),
            CellPreviewWidth = ParseDouble(cellPreviewWidthRaw),
            Theme = themeRaw ?? "system",
            CheckForUpdates = !bool.TryParse(checkForUpdatesRaw, out var checkForUpdates)
                || checkForUpdates,
        };

        var spaces = new List<SpaceState>();
        var spacesById = new Dictionary<string, SpaceState>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT id, name, COALESCE(color, ''), provider_id, active_database, last_active_tab_id, COALESCE(icon, ''), COALESCE(hidden, 0)
                FROM spaces
                ORDER BY sort_order, name;
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var space = new SpaceState
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    Color = reader.GetString(2),
                    ProviderId = reader.GetString(3),
                    ActiveDatabase = reader.IsDBNull(4) ? null : reader.GetString(4),
                    ActiveTabId = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Icon = reader.GetString(6) is { Length: > 0 } icon ? icon : null,
                    Hidden = reader.GetInt64(7) != 0,
                    Profile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                };
                spaces.Add(space);
                spacesById[space.Id] = space;
            }
        }

        if (spaces.Count > 0)
        {
            ReadAllProfiles(connection, spacesById);
            ReadAllGroups(connection, spacesById);
            ReadAllTabs(connection, spacesById);
        }

        state.Spaces = spaces;

        // A saved active id can outlive the space it points at (an imported or partly reset store).
        // Without this guard the shell opens on onboarding even though the spaces are still here.
        if (spaces.Count == 0)
        {
            state.ActiveSpaceId = null;
        }
        else if (string.IsNullOrEmpty(state.ActiveSpaceId) || !spacesById.ContainsKey(state.ActiveSpaceId))
        {
            state.ActiveSpaceId = spaces[0].Id;
        }

        state.ArchivedTabs = ReadArchivedTabs(connection);
        return state;
    }

    private static Dictionary<string, string> ReadAllState(SqliteConnection connection)
    {
        var values = new Dictionary<string, string>(16, StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM app_state;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            values[reader.GetString(0)] = reader.GetString(1);
        }

        return values;
    }

    private static void ReadAllProfiles(SqliteConnection connection, Dictionary<string, SpaceState> spacesById)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT space_id, key, value FROM space_settings;";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (spacesById.TryGetValue(reader.GetString(0), out var space))
            {
                space.Profile[reader.GetString(1)] = reader.GetString(2);
            }
        }
    }

    private static void ReadAllGroups(SqliteConnection connection, Dictionary<string, SpaceState> spacesById)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT space_id, id, name, COALESCE(color, ''), COALESCE(pinned, 0), COALESCE(expanded, 1),
                   COALESCE(database, '')
            FROM tab_groups
            ORDER BY sort_order;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!spacesById.TryGetValue(reader.GetString(0), out var space))
            {
                continue;
            }

            space.Groups.Add(new TabGroupState
            {
                Id = reader.GetString(1),
                Name = reader.GetString(2),
                Color = reader.GetString(3),
                Pinned = reader.GetInt64(4) != 0,
                Expanded = reader.GetInt64(5) != 0,
                Database = reader.GetString(6) is { Length: > 0 } database ? database : null,
            });
        }
    }

    private static void ReadAllTabs(SqliteConnection connection, Dictionary<string, SpaceState> spacesById)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT space_id, id, title, COALESCE(database, ''), COALESCE(content, ''),
                   COALESCE(pinned, 0), COALESCE(last_accessed_at, ''), COALESCE(group_id, '')
            FROM tabs
            ORDER BY sort_order;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!spacesById.TryGetValue(reader.GetString(0), out var space))
            {
                continue;
            }

            space.Tabs.Add(new TabState
            {
                Id = reader.GetString(1),
                Title = reader.GetString(2),
                Database = reader.GetString(3),
                Content = reader.GetString(4),
                Pinned = reader.GetInt64(5) != 0,
                // A tab saved before the inactivity clock existed counts as used now, so the
                // first launch after the upgrade does not sweep the whole workspace.
                LastAccessedAt = ParseTimestamp(reader.GetString(6)),
                GroupId = reader.GetString(7) is { Length: > 0 } groupId ? groupId : null,
            });
        }
    }

    private static List<ArchivedTabState> ReadArchivedTabs(SqliteConnection connection)
    {
        var tabs = new List<ArchivedTabState>();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, space_id, space_name, COALESCE(color, ''), title,
                   COALESCE(database, ''), COALESCE(content, ''), COALESCE(pinned, 0),
                   COALESCE(archived_at, ''), COALESCE(group_name, '')
            FROM archived_tabs
            ORDER BY archived_at DESC;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            tabs.Add(new ArchivedTabState
            {
                Id = reader.GetString(0),
                SpaceId = reader.GetString(1),
                SpaceName = reader.GetString(2),
                Color = reader.GetString(3),
                Title = reader.GetString(4),
                Database = reader.GetString(5),
                Content = reader.GetString(6),
                Pinned = reader.GetInt64(7) != 0,
                ArchivedAt = ParseTimestamp(reader.GetString(8)),
                GroupName = reader.GetString(9) is { Length: > 0 } groupName ? groupName : null,
            });
        }

        return tabs;
    }

    private static DateTimeOffset ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;

    // ---------------------------------------------------------------- write

    private static void Write(SqliteConnection connection, SqliteTransaction transaction, StudioState state)
    {
        var spaceIds = new HashSet<string>(state.Spaces.Count, StringComparer.Ordinal);
        var spaceOrder = 0;

        // Reuse one prepared command per statement type across the whole transaction instead of
        // re-preparing SQLite statements on every row.
        using var upsertSpace = connection.CreateCommand();
        upsertSpace.Transaction = transaction;
        upsertSpace.CommandText =
            """
            INSERT INTO spaces (id, name, color, icon, hidden, provider_id, active_database, last_active_tab_id, sort_order)
            VALUES ($id, $name, $color, $icon, $hidden, $provider, $database, $tab, $sort)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                color = excluded.color,
                icon = excluded.icon,
                hidden = excluded.hidden,
                provider_id = excluded.provider_id,
                active_database = excluded.active_database,
                last_active_tab_id = excluded.last_active_tab_id,
                sort_order = excluded.sort_order,
                updated_at = datetime('now');
            """;
        var sId = upsertSpace.Parameters.Add("$id", SqliteType.Text);
        var sName = upsertSpace.Parameters.Add("$name", SqliteType.Text);
        var sColor = upsertSpace.Parameters.Add("$color", SqliteType.Text);
        var sIcon = upsertSpace.Parameters.Add("$icon", SqliteType.Text);
        var sHidden = upsertSpace.Parameters.Add("$hidden", SqliteType.Integer);
        var sProvider = upsertSpace.Parameters.Add("$provider", SqliteType.Text);
        var sDatabase = upsertSpace.Parameters.Add("$database", SqliteType.Text);
        var sTab = upsertSpace.Parameters.Add("$tab", SqliteType.Text);
        var sSort = upsertSpace.Parameters.Add("$sort", SqliteType.Integer);

        using var deleteProfile = connection.CreateCommand();
        deleteProfile.Transaction = transaction;
        deleteProfile.CommandText = "DELETE FROM space_settings WHERE space_id = $id;";
        var dpId = deleteProfile.Parameters.Add("$id", SqliteType.Text);

        using var insertProfile = connection.CreateCommand();
        insertProfile.Transaction = transaction;
        insertProfile.CommandText = "INSERT INTO space_settings (space_id, key, value) VALUES ($id, $key, $value);";
        var ipId = insertProfile.Parameters.Add("$id", SqliteType.Text);
        var ipKey = insertProfile.Parameters.Add("$key", SqliteType.Text);
        var ipValue = insertProfile.Parameters.Add("$value", SqliteType.Text);

        using var deleteGroups = connection.CreateCommand();
        deleteGroups.Transaction = transaction;
        deleteGroups.CommandText = "DELETE FROM tab_groups WHERE space_id = $id;";
        var dgId = deleteGroups.Parameters.Add("$id", SqliteType.Text);

        using var upsertGroup = connection.CreateCommand();
        upsertGroup.Transaction = transaction;
        upsertGroup.CommandText =
            """
            INSERT INTO tab_groups (id, space_id, name, color, pinned, expanded, database, sort_order)
            VALUES ($id, $space, $name, $color, $pinned, $expanded, $database, $sort)
            ON CONFLICT(id) DO UPDATE SET
                space_id = excluded.space_id,
                name = excluded.name,
                color = excluded.color,
                pinned = excluded.pinned,
                expanded = excluded.expanded,
                database = excluded.database,
                sort_order = excluded.sort_order;
            """;
        var gId = upsertGroup.Parameters.Add("$id", SqliteType.Text);
        var gSpace = upsertGroup.Parameters.Add("$space", SqliteType.Text);
        var gName = upsertGroup.Parameters.Add("$name", SqliteType.Text);
        var gColor = upsertGroup.Parameters.Add("$color", SqliteType.Text);
        var gPinned = upsertGroup.Parameters.Add("$pinned", SqliteType.Integer);
        var gExpanded = upsertGroup.Parameters.Add("$expanded", SqliteType.Integer);
        var gDatabase = upsertGroup.Parameters.Add("$database", SqliteType.Text);
        var gSort = upsertGroup.Parameters.Add("$sort", SqliteType.Integer);

        using var upsertTab = connection.CreateCommand();
        upsertTab.Transaction = transaction;
        upsertTab.CommandText =
            """
            INSERT INTO tabs (id, space_id, title, database, content, pinned, last_accessed_at, group_id, sort_order)
            VALUES ($id, $space, $title, $database, $content, $pinned, $accessed, $group, $sort)
            ON CONFLICT(id) DO UPDATE SET
                space_id = excluded.space_id,
                title = excluded.title,
                database = excluded.database,
                content = excluded.content,
                pinned = excluded.pinned,
                last_accessed_at = excluded.last_accessed_at,
                group_id = excluded.group_id,
                sort_order = excluded.sort_order,
                updated_at = datetime('now');
            """;
        var tId = upsertTab.Parameters.Add("$id", SqliteType.Text);
        var tSpace = upsertTab.Parameters.Add("$space", SqliteType.Text);
        var tTitle = upsertTab.Parameters.Add("$title", SqliteType.Text);
        var tDatabase = upsertTab.Parameters.Add("$database", SqliteType.Text);
        var tContent = upsertTab.Parameters.Add("$content", SqliteType.Text);
        var tPinned = upsertTab.Parameters.Add("$pinned", SqliteType.Integer);
        var tAccessed = upsertTab.Parameters.Add("$accessed", SqliteType.Text);
        var tGroup = upsertTab.Parameters.Add("$group", SqliteType.Text);
        var tSort = upsertTab.Parameters.Add("$sort", SqliteType.Integer);

        using var selectSpaceTabIds = connection.CreateCommand();
        selectSpaceTabIds.Transaction = transaction;
        selectSpaceTabIds.CommandText = "SELECT id FROM tabs WHERE space_id = $id;";
        var stSpaceId = selectSpaceTabIds.Parameters.Add("$id", SqliteType.Text);

        var tabIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var space in state.Spaces)
        {
            spaceIds.Add(space.Id);
            sId.Value = space.Id;
            sName.Value = space.Name;
            sColor.Value = space.Color;
            sIcon.Value = (object?)space.Icon ?? DBNull.Value;
            sHidden.Value = space.Hidden ? 1 : 0;
            sProvider.Value = space.ProviderId;
            sDatabase.Value = (object?)space.ActiveDatabase ?? DBNull.Value;
            sTab.Value = (object?)space.ActiveTabId ?? DBNull.Value;
            sSort.Value = spaceOrder++;
            upsertSpace.ExecuteNonQuery();

            dpId.Value = space.Id;
            deleteProfile.ExecuteNonQuery();
            foreach (var pair in space.Profile)
            {
                ipId.Value = space.Id;
                ipKey.Value = pair.Key;
                ipValue.Value = pair.Value;
                insertProfile.ExecuteNonQuery();
            }

            // Groups are small and few; replacing the space's set keeps ordering and deletions in one
            // statement pair instead of tracking a diff.
            dgId.Value = space.Id;
            deleteGroups.ExecuteNonQuery();
            var groupOrder = 0;
            foreach (var group in space.Groups)
            {
                gId.Value = group.Id;
                gSpace.Value = space.Id;
                gName.Value = group.Name;
                gColor.Value = group.Color;
                gPinned.Value = group.Pinned ? 1 : 0;
                gExpanded.Value = group.Expanded ? 1 : 0;
                gDatabase.Value = (object?)group.Database ?? DBNull.Value;
                gSort.Value = groupOrder++;
                upsertGroup.ExecuteNonQuery();
            }

            tabIds.Clear();
            var tabOrder = 0;
            foreach (var tab in space.Tabs)
            {
                tabIds.Add(tab.Id);
                tId.Value = tab.Id;
                tSpace.Value = space.Id;
                tTitle.Value = tab.Title;
                tDatabase.Value = tab.Database;
                tContent.Value = tab.Content;
                tPinned.Value = tab.Pinned ? 1 : 0;
                tAccessed.Value = (tab.LastAccessedAt ?? DateTimeOffset.UtcNow).ToString("o", CultureInfo.InvariantCulture);
                tGroup.Value = (object?)tab.GroupId ?? DBNull.Value;
                tSort.Value = tabOrder++;
                upsertTab.ExecuteNonQuery();
            }

            stSpaceId.Value = space.Id;
            List<string>? tabsToDelete = null;
            using (var reader = selectSpaceTabIds.ExecuteReader())
            {
                while (reader.Read())
                {
                    var id = reader.GetString(0);
                    if (!tabIds.Contains(id))
                    {
                        tabsToDelete ??= [];
                        tabsToDelete.Add(id);
                    }
                }
            }

            if (tabsToDelete is not null)
            {
                using var deleteTab = connection.CreateCommand();
                deleteTab.Transaction = transaction;
                deleteTab.CommandText = "DELETE FROM tabs WHERE id = $id;";
                var dtId = deleteTab.Parameters.Add("$id", SqliteType.Text);
                foreach (var id in tabsToDelete)
                {
                    dtId.Value = id;
                    deleteTab.ExecuteNonQuery();
                }
            }
        }

        var storedSpaces = SelectIds(connection, transaction, "SELECT id FROM spaces;");
        foreach (var id in storedSpaces)
        {
            if (!spaceIds.Contains(id))
            {
                Execute(connection, transaction, "DELETE FROM spaces WHERE id = $id;", ("$id", id));
            }
        }

        using var upsertState = connection.CreateCommand();
        upsertState.Transaction = transaction;
        upsertState.CommandText =
            """
            INSERT INTO app_state (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = datetime('now');
            """;
        var stKey = upsertState.Parameters.Add("$key", SqliteType.Text);
        var stValue = upsertState.Parameters.Add("$value", SqliteType.Text);

        void SetAppState(string key, string value)
        {
            stKey.Value = key;
            stValue.Value = value;
            upsertState.ExecuteNonQuery();
        }

        SetAppState("active_space_id", state.ActiveSpaceId ?? string.Empty);
        SetAppState("query_counter", state.QueryCounter.ToString(CultureInfo.InvariantCulture));
        SetAppState("auto_archive_enabled", state.AutoArchiveEnabled ? "true" : "false");
        SetAppState("auto_archive_days", state.AutoArchiveDays.ToString(CultureInfo.InvariantCulture));
        SetAppState("notes_pane_open", state.NotesPaneOpen ? "true" : "false");
        SetAppState("theme", AppThemeText.ToStorage(AppThemeText.Parse(state.Theme)));
        SetAppState("check_for_updates", state.CheckForUpdates ? "true" : "false");
        if (state.ResultsPaneHeight is { } resultsHeight)
        {
            SetAppState("results_pane_height", resultsHeight.ToString("R", CultureInfo.InvariantCulture));
        }

        if (state.CellPreviewWidth is { } previewWidth)
        {
            SetAppState("cell_preview_width", previewWidth.ToString("R", CultureInfo.InvariantCulture));
        }

        if (state.WindowBounds is { } bounds)
        {
            SetAppState("window_bounds", JsonSerializer.Serialize(bounds, StudioJsonContext.Default.WindowBoundsState));
        }

        WriteArchivedTabs(connection, transaction, state.ArchivedTabs);

        // Notes outlive archiving but not deletion: keep the ones whose tab is in either table,
        // drop the rest (a closed workspace, a tab deleted from the archive). This is also the
        // cleanup path for an install that predates notes.
        Execute(
            connection,
            transaction,
            """
            DELETE FROM notes
            WHERE tab_id NOT IN (SELECT id FROM tabs UNION SELECT id FROM archived_tabs);
            """);
    }

    private static void WriteArchivedTabs(
        SqliteConnection connection,
        SqliteTransaction transaction,
        List<ArchivedTabState> tabs)
    {
        var tabIds = new HashSet<string>(tabs.Count, StringComparer.Ordinal);

        using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText =
            """
            INSERT INTO archived_tabs (id, space_id, space_name, color, title, database, content, pinned, archived_at, group_name)
            VALUES ($id, $space, $space_name, $color, $title, $database, $content, $pinned, $archived, $group)
            ON CONFLICT(id) DO UPDATE SET
                space_id = excluded.space_id,
                space_name = excluded.space_name,
                color = excluded.color,
                title = excluded.title,
                database = excluded.database,
                content = excluded.content,
                pinned = excluded.pinned,
                archived_at = excluded.archived_at,
                group_name = excluded.group_name;
            """;
        var aId = upsert.Parameters.Add("$id", SqliteType.Text);
        var aSpace = upsert.Parameters.Add("$space", SqliteType.Text);
        var aSpaceName = upsert.Parameters.Add("$space_name", SqliteType.Text);
        var aColor = upsert.Parameters.Add("$color", SqliteType.Text);
        var aTitle = upsert.Parameters.Add("$title", SqliteType.Text);
        var aDatabase = upsert.Parameters.Add("$database", SqliteType.Text);
        var aContent = upsert.Parameters.Add("$content", SqliteType.Text);
        var aPinned = upsert.Parameters.Add("$pinned", SqliteType.Integer);
        var aArchived = upsert.Parameters.Add("$archived", SqliteType.Text);
        var aGroup = upsert.Parameters.Add("$group", SqliteType.Text);

        foreach (var tab in tabs)
        {
            tabIds.Add(tab.Id);
            aId.Value = tab.Id;
            aSpace.Value = tab.SpaceId;
            aSpaceName.Value = tab.SpaceName;
            aColor.Value = tab.Color;
            aTitle.Value = tab.Title;
            aDatabase.Value = tab.Database;
            aContent.Value = tab.Content;
            aPinned.Value = tab.Pinned ? 1 : 0;
            aArchived.Value = tab.ArchivedAt.ToString("o", CultureInfo.InvariantCulture);
            aGroup.Value = (object?)tab.GroupName ?? DBNull.Value;
            upsert.ExecuteNonQuery();
        }

        var storedTabs = SelectIds(connection, transaction, "SELECT id FROM archived_tabs;");
        foreach (var id in storedTabs)
        {
            if (!tabIds.Contains(id))
            {
                Execute(connection, transaction, "DELETE FROM archived_tabs WHERE id = $id;", ("$id", id));
            }
        }
    }

    private static Dictionary<string, string> ReadProfile(SqliteConnection connection, string spaceId)
    {
        var profile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM space_settings WHERE space_id = $id;";
        command.Parameters.AddWithValue("$id", spaceId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            profile[reader.GetString(0)] = reader.GetString(1);
        }

        return profile;
    }

    private static void SetState(SqliteConnection connection, SqliteTransaction? transaction, string key, string value) =>
        Execute(
            connection,
            transaction,
            """
            INSERT INTO app_state (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = datetime('now');
            """,
            ("$key", key),
            ("$value", value));

    private static string? ReadState(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_state WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static double? ParseDouble(string? raw) =>
        double.TryParse(
            raw,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;

    private static WindowBoundsState? ParseWindowBounds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(raw, StudioJsonContext.Default.WindowBoundsState);
        }
        catch (JsonException)
        {
            // A truncated or hand-edited value must not block the launch; the window just centers.
            return null;
        }
    }

    // ---------------------------------------------------------------- legacy import

    private StudioState? TryImportLegacyJson(SqliteConnection connection)
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        var legacy = Path.Combine(directory, "studio.json");
        if (!File.Exists(legacy))
        {
            return null;
        }

        StudioState? state;
        try
        {
            state = JsonSerializer.Deserialize(File.ReadAllText(legacy), StudioJsonContext.Default.StudioState);
        }
        catch
        {
            return null;
        }

        if (state is null || state.Spaces.Count == 0)
        {
            return null;
        }

        using (var transaction = connection.BeginTransaction())
        {
            Write(connection, transaction, state);
            transaction.Commit();
        }

        try
        {
            File.Move(legacy, legacy + ".imported", overwrite: true);
        }
        catch
        {
            // The import succeeded; an un-movable source file is not worth failing over.
        }

        return state;
    }

    // ---------------------------------------------------------------- helpers

    private static int Execute(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static List<string> SelectIds(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string? parameter = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (parameter is not null)
        {
            command.Parameters.AddWithValue("$id", parameter);
        }

        var ids = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }
}
